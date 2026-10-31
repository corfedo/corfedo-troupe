# Operations example

One executable serves an API workload and two operation owners, Gateway and AI, in any grouping. The API
calls both owners through one keyed `IGateway`: in process when the owner is composed here, otherwise over
HTTP/2 with mutual TLS to the owner's internal listener. Troupe supplies only composition facts; the
catalogue, routing, transport, serialization and caller identity are application code in this example.
It follows the structure of Corfedo Platform's local and remote calls, with HTTP/2 and JSON in place of
the platform's gRPC and MessagePack.

| Profile | `--roles` | Calls |
| --- | --- | --- |
| `combined` | `ai,api,gateway` | All local; no addresses or caller certificate required |
| `api` | `api` | Gateway and AI remote |
| `gateway` | `gateway` | Serves Gateway operations on `internal` only |
| `ai` | `ai` | Serves AI operations on `internal` only |
| `api-gateway` | `api,gateway` | Gateway local, AI remote; only AI's address is required |

## Structure

| Code | Role |
| --- | --- |
| `Contracts/` | Workload names, operation requests and responses, handler interface |
| `Catalogue/` | The operation table and owner-to-host table, the part a platform would generate, and the invoker that applies the caller policy |
| `Hosts/` | `GatewayHost` and `AiHost`: each registers its owner's handlers and requires the shared catalogue and the internal listener |
| `Listener/` | `InternalListener`: `KestrelEndpoint("internal", ...)` with client certificates pinned by fingerprint, an admission stage and the operation route |
| `Calls/` | `Calls.Declare` for a caller's destinations, the keyed gateway, its mutual-TLS clients and the catalogue rule |

`InternalListener` is composed once per process that hosts an exposed owner, so the listener exists
because an owner is hosted, not because a port is configured. Operation routes answer only on it, and it
answers only operation routes. Callers are identified by their client certificate, never by the payload;
the same caller policy applies to local and remote calls.

`ApiCalls` declares each destination with `Calls.Declare`. For each owner it records
`ReadsConfigurationUnless<THost>` for `Roles:Remote:<owner>`, `Workload:Identities:api` and
`Workload:Root`, so a process missing an address fails during Compose with TRP005. It also registers a
keyed `Destination<THost>`, which `CallsRule` reads from the manifest: OPS001 reports an owner the caller
may call but does not declare, OPS002 a declared owner that exposes nothing to it. The gateway chooses each
route once with `IComposition.Includes<THost>()`.

## Running

Generate development certificates into the build output, then start the combined process from the
repository root:

```sh
dotnet run --project examples/Operations -- certificates
dotnet run --project examples/Operations -- --roles ai,api,gateway \
  --Kestrel:Endpoints:public:Url http://127.0.0.1:5082 --Kestrel:Endpoints:internal:Url https://127.0.0.1:5083
curl http://127.0.0.1:5082/workspaces/w1
curl http://127.0.0.1:5082/routes
```

`/routes` shows how the API reaches each owner. Split the same workloads into three processes:

```sh
dotnet run --project examples/Operations -- --roles gateway --Kestrel:Endpoints:internal:Url https://127.0.0.1:5083
dotnet run --project examples/Operations -- --roles ai --Kestrel:Endpoints:internal:Url https://127.0.0.1:5084
dotnet run --project examples/Operations -- --roles api --Kestrel:Endpoints:public:Url http://127.0.0.1:5082 \
  --Roles:Remote:gateway https://127.0.0.1:5083 --Roles:Remote:ai https://127.0.0.1:5084 \
  --Workload:Identities:api:Certificate certificates/api.pem --Workload:Identities:api:Key certificates/api.key \
  --Workload:Root certificates/root.pem
```

Each process declares only the listeners it serves; Kestrel endpoints are supplied per process because
Troupe rejects configured endpoints that the selected profile does not request. `appsettings.json` holds
the shared server certificate (`Kestrel:Certificates:Default`) and the trusted callers (`Internal:Callers`).
Certificate paths are relative to the content root, the build output directory.

## Tests

`OperationsExampleTests` verifies every profile and starts real Kestrel hosts on loopback port 0:
local calls in the combined process, split and hybrid processes over mutual TLS, route isolation between
the listeners, rejection of missing and unknown client certificates, the caller policy, TRP005 for a
missing address and both catalogue rule directions.
