# Wolverine messaging example

One executable serves orders, notifications, or both. Each role contributes its own handler discovery
and local queue configuration to `WolverineOptions`; the application applies all contributions inside
one `IHostApplicationBuilder.UseWolverine` callback. Shared components own the journal, scoped processor,
receipt handler and HTTP ingress. Wolverine 6.41.0 and its runtime compiler are example dependencies.

| `--profile` | Roles | Serving behavior |
| --- | --- | --- |
| `orders` | `orders` | Order request/reply, queued order submission and receipt lookup |
| `notifications` | `notifications` | Notification request/reply and receipt lookup |
| `combined` | `orders,notifications` | Both workflows in one process |

From the repository root:

```sh
dotnet run --project examples/Messaging -- --profile combined
```

The configured address is `http://127.0.0.1:5081`. Configuration uses standard IConfiguration providers;
for example, override the listener with `--Kestrel:Endpoints:public:Url http://127.0.0.1:5082`.

```sh
curl http://127.0.0.1:5081/orders -H 'Content-Type: application/json' -d '{"id":"order-1"}'
curl http://127.0.0.1:5081/notifications -H 'Content-Type: application/json' -d '{"id":"notice-1","recipient":"reader@example.test"}'
curl http://127.0.0.1:5081/orders/queued -H 'Content-Type: application/json' -d '{"id":"order-2"}'
curl http://127.0.0.1:5081/receipts/order-2
```

A queued submission returns HTTP 202 with its receipt URL. Poll that URL until it returns HTTP 200;
a missing receipt returns HTTP 404. Use a distinct ID for each submission. Receipts include the consumer
and processor scope identity, allowing separate message executions to be inspected. In the default mode, queues and receipts
live only in this process. PostgreSQL transport uses the explicit durable mode below.

## Static handlers and generation

Serving uses `TypeLoadMode.Static` and the committed files under `Internal/Generated`. Missing generated
handler adapters fail startup even though the runtime compiler package is present. Regenerate artifacts
when handler signatures, dependencies or middleware change:

```sh
cd examples/Messaging
rm -rf Internal/Generated
dotnet run -- codegen write
cd ../..
dotnet build Corfedo.Troupe.slnx
dotnet test Corfedo.Troupe.slnx
```

Generation uses a separate generic host with an explicit union of consumer components and their serving
lifetimes. It composes neither serving role nor HTTP ingress and does not start the host. Its command
writes all three handler adapters and a registry. Run the CLI as a separate process: JasperFx's codegen
command changes process-wide state, and running serving hosts in the same process afterward can put
Wolverine into metadata-only mode.

The [Wolverine code-generation guide](https://wolverinefx.net/guide/codegen) describes static loading and
regeneration. [Explicit discovery](https://wolverinefx.net/guide/handlers/discovery) keeps selectable
consumer ownership visible; disabling conventional discovery prevents unrelated handlers from being selected.

## Verification

`Corfedo.Troupe.Examples.Tests` uses these application builders and composition definitions. It verifies
every declared serving profile, then starts real Kestrel hosts to exercise request/reply and local-queue
handling. Tests assert scoped processor disposal, role-specific dependencies and HTTP endpoint exclusion.
The fixture assembly in `tests/Fixtures/IncompleteMessagingArtifact` compiles order/receipt adapters but
omits the notification adapter, proving that incomplete static artifacts fail startup. Generation is
exercised through the actual CLI in an isolated process and temporary output directory.

## PostgreSQL transport

The `durable` command uses the same handlers, scoped processors and generated adapters with PostgreSQL
inbox/outbox storage and transport. `DurableMessagingComposition` declares four exact serving profiles:

| `--profile` | Behavior |
| --- | --- |
| `publisher` | HTTP submissions at `/orders/queued` and `/notifications/queued`; no consumers |
| `orders` | PostgreSQL orders listener and `/receipts/{id}` |
| `notifications` | PostgreSQL notifications listener and `/receipts/{id}` |
| `combined` | Both listeners and receipt lookup |

Set `Messaging:ConnectionString` through normal configuration. Use a dedicated example database;
setup creates Wolverine tables in `troupe_messages` and `troupe_queues`. The provisioning command
composes an explicit union of the two queues without HTTP ingress and does not start the host.
For a disposable local database matching the integration-test image:

```sh
docker run --detach --name troupe-example-postgres --publish 127.0.0.1:55432:5432 -e POSTGRES_DB=troupe -e POSTGRES_PASSWORD=example-only postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722
```

Wait for PostgreSQL to accept connections, then run setup before starting any serving process:

```sh
export Messaging__ConnectionString='Host=127.0.0.1;Port=55432;Database=troupe;Username=postgres;Password=example-only'
dotnet run --project examples/Messaging -- durable resources setup
```

In separate terminals with the same connection configuration, start a publisher and a consumer:

```sh
dotnet run --project examples/Messaging -- durable --profile publisher --Kestrel:Endpoints:public:Url http://127.0.0.1:5081
dotnet run --project examples/Messaging -- durable --profile combined --Kestrel:Endpoints:public:Url http://127.0.0.1:5082
```

```sh
curl http://127.0.0.1:5081/orders/queued -H 'Content-Type: application/json' -d '{"id":"durable-order-1"}'
curl http://127.0.0.1:5082/receipts/durable-order-1
```

Submissions return HTTP 202; poll the consumer for HTTP 200. The publisher does not expose receipts.
Serving disables automatic message-storage creation and transport auto-provisioning. Missing storage
fails startup; schema creation belongs to the explicit setup command and its database credentials.
Queued messages survive consumer absence and process restart. Receipts remain an in-memory observation
of completed processing and disappear with the consumer. This example does not provide durable business
state, exactly-once business effects, or a transactional business outbox.

The [Wolverine PostgreSQL guide](https://wolverinefx.net/guide/durability/postgresql) documents the transport
and persistence APIs. `durable` is an example application adapter; Troupe runtime packages have no
PostgreSQL or Wolverine dependency. Static handler regeneration uses the same `codegen write` command
as the local mode because both modes share handler chains and dependency lifetimes.

The durability tests require Docker. Testcontainers creates and disposes an isolated PostgreSQL container
using the image above; tests never use the shared development database. They verify all durable profiles
with an explicit `NullMessageStore` substitution for build-only verification. This substitution does not
make arbitrary Build or options callbacks an effects sandbox. Started tests run the real setup command,
check missing-schema failure without schema creation, reject incomplete static artifacts, exercise each
consumer profile across process boundaries, inspect persisted queue rows, terminate the publisher and
consumer processes, and recover queued messages in a new combined consumer process. They cover queued
recovery while consumers are offline, rather than interruption during an in-flight business transaction.
Child processes and containers are disposed on failure and cancellation.

Remove the named disposable example container and its data after use:

```sh
docker rm --force --volumes troupe-example-postgres
```

The Corfedo adapter, platform-generated handler lifetimes and platform cutover require separate scope
and dependency decisions.
