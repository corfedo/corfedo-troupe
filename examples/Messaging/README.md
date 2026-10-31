# Wolverine messaging example

One executable serves orders, notifications, or both. Each role contributes its own handler discovery
and local queue configuration to `WolverineOptions`; the application applies all contributions inside
one `IHostApplicationBuilder.UseWolverine` callback. Shared components own the journal, scoped processor,
receipt handler and HTTP ingress. Wolverine 6.41.0 and its runtime compiler are example dependencies.

| `--roles` | Serving behavior |
| --- | --- |
| `orders` | Order request/reply, queued order submission and receipt lookup |
| `notifications` | Notification request/reply and receipt lookup |
| `orders,notifications` | Both workflows in one process |

From the repository root:

```sh
dotnet run --project examples/Messaging -- --roles orders,notifications
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
and processor scope identity, allowing separate message executions to be inspected. Queues and receipts
live only in this process and are not durable. This example supplies no remote transport or database.

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

These tests cover the standalone example. PostgreSQL durability, the Corfedo adapter, platform-generated
handler lifetimes and the platform cutover require their separate implementation scope and dependency decisions.
