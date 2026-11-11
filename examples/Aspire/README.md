# Aspire example

An Aspire AppHost that runs the [Operations example](../Operations) with `Corfedo.Troupe.Aspire.Hosting`:

| `--Topology` | Resources |
| --- | --- |
| `combined` (default) | `operations` with profile `combined` |
| `split` | `gateway`, `ai` and `api`, each with its own profile; `api` calls the other two over their `internal` listeners |

The `certificates` resource runs the example's `certificates` command first and the other resources wait for it.
The package adds the `public` endpoint. `internal` uses mutual TLS with the example's certificates, so the AppHost
declares it with `WithHttpsEndpoint` and `WithoutHttpsCertificate`. In `split`, `api` gets the owners' addresses
from their endpoints and its client certificate paths as literals:

```csharp
Operations("api").WithTroupeProfile("api")
    .WithEnvironment("Roles__Remote__gateway", gateway.GetEndpoint("internal"))
    .WithEnvironment("Roles__Remote__ai", ai.GetEndpoint("internal"))
    .WithEnvironment("Workload__Identities__api__Certificate", "certificates/api.pem")
    .WithEnvironment("Workload__Identities__api__Key", "certificates/api.key")
    .WithEnvironment("Workload__Root", "certificates/root.pem");
```

Run it from the repository root with the Aspire CLI, or with `dotnet run`:

```sh
aspire run --apphost examples/Aspire/AppHost.csproj -- --Topology split
dotnet run --project examples/Aspire --launch-profile Split
```

`AspireExampleTests` starts both topologies through `Aspire.Hosting.Testing` and calls `/workspaces/w1` and
`/routes` on the API.
