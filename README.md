# Corfedo Troupe

Troupe is a .NET 10 alpha framework for selecting application roles from one executable.
Roles declare services and shared components. Troupe resolves only the selected closure,
composes dependencies once, and records who contributed each registration.

| Package | Implements |
| --- | --- |
| `Corfedo.Troupe` | Roles, components, strict profiles, graph ordering, DI provenance, options, builder contributions and startup checks |
| `Corfedo.Troupe.AspNetCore` | Application-declared middleware stages, endpoint owner groups and named Kestrel requirements |
| `Corfedo.Troupe.Testing` | Real generic/web profile verification, explicit test overrides and host disposal; no test runner dependency |

Reference the relevant project in `src/`; packages are not published. Use SDK 10.0.300:

```sh
dotnet build Corfedo.Troupe.slnx
dotnet test Corfedo.Troupe.slnx
dotnet run --project examples/Worker -- --roles worker
dotnet run --project examples/Web -- --roles web,health
```

The [worker](examples/Worker/Program.cs) integrates Microsoft.Extensions.Hosting, validated options
and BackgroundService. The [web example](examples/Web/Program.cs) integrates ASP.NET Core routing,
Minimal APIs, health checks and named Kestrel configuration at `http://127.0.0.1:5080`.

## Application composition

```csharp
[Role("worker")]
public sealed class WorkerRole : IRole
{
    public void Compose(IRoleBuilder role)
    {
        role.Requires<Storage>();
        role.Services(services => services.AddHostedService<Worker>());
    }
}

public sealed class AppComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition) =>
        definition.Role<WorkerRole>().Profile("worker", "worker");
}

builder.Compose<AppComposition>();
// Apply application adapters and builder.ApplyContributions<T>(target) here.
builder.FinalizeComposition();
using var host = builder.Build();
await host.RunAsync();
```

`Roles` is a comma-separated IConfiguration value (`--roles worker` or `ROLES=worker`). Selection is
a set. Dependencies precede dependents; ordinal names break ready-node ties. Declared profiles are
exact supported sets, with no opt-out. Without profiles, any nonempty known role set is accepted.
Stateless, parameterless definitions only declare: no I/O or resource acquisition. Recorded callbacks
execute after graph validation. Runtime consumers inject `IComposition` for membership and snapshots.

Keep `ValidateOnBuild` and `ValidateScopes` enabled, as the examples do. Finalize after adapters and
before Build to capture application registrations and reject unconsumed contributions. This early call is
optional; pipeline application and startup use the same session-owned finalization operation. Each exact
builder target type is applied once. Declaration builders close when their Compose callbacks return.
A failed composition cannot be reused; callback failures retain their owner, original exception and
failure manifest. Runtime IComposition exposes successfully validated snapshots, separately from the build
session. Completed declaration and execution callbacks are released when their work ends.

`Options<T>(section)` binds one owned section per type and adds required presence validation and
ValidateOnStart. A second declaration is an error. `Options<T,TValidator>` explicitly registers a
DI-constructed validator; additional validators compose through DI. `ReadsConfiguration` records manual
section intent. Required absence fails composition and is also validated at Value resolution and startup.
The manifest contains paths and owners, never bound values. Disposable runtime resources belong in DI
factories/type registrations; captured instances and pre-Build adapter resources remain application-owned.

`Check` callbacks run sequentially in Troupe's IHostedLifecycleService.StartingAsync. Host lifetime work,
hosted-service constructors and startup options validation precede them. Other lifecycle callbacks can
precede or overlap checks. All StartingAsync callbacks complete before hosted-service StartAsync and
Kestrel binding. An earlier barrier remains an explicit application operation. Roles and components log
once in StartedAsync.

## ASP.NET and verification

Declare application stage names with `Stages(...)`, including an explicit root UseRouting callback before
endpoint-aware middleware. Own root authentication and authorization explicitly. Conditional UseWhen
authentication needs the application adapter's version-sensitive `__AuthenticationMiddlewareSet` marker;
Troupe does not suppress authentication. `app.UseComposition()` applies stages once, then maps empty-prefix
endpoint owner groups. Startup and profile verification reject recorded pipelines that were not applied.
Endpoint inspection captures the inventory at UseComposition: map application endpoints before that call,
or declare them through Troupe. `composition.AspNetCore()` exposes endpoint metadata and listener requirements.

`KestrelEndpoint(name)` requires `Kestrel:Endpoints:<name>`. Missing and unrequested configured names
are errors. After Compose, install customization with `builder.ConfigureCompositionEndpoint(name, callback)`.
A second tracked callback is rejected before Kestrel replaces TLS policy. Resolve trust through
`ListenOptions.ApplicationServices`. The loader must read the `Kestrel` configuration section. Initialize it
once with `reloadOnChange: false`, before callbacks, as the example does. Topology changes require restart; raw Kestrel calls remain untracked.
Actual bind conflicts belong to Kestrel.

```csharp
var result = await CompositionVerifier.VerifyProfilesAsync<AppComposition>(
    profile => CreateIsolatedBuilder(profile),
    (profile, services) => ReplaceExternalResources(services));
// Assert result.Succeeded with your test runner; result.ToString() includes failures.
```

Supply a fresh HostApplicationBuilder or WebApplicationBuilder per declared profile. Verification composes,
applies privileged test-owned overrides, preserves the application provider factory, builds, resolves declared
options, maps web endpoints and disposes every built host asynchronously when available. The supplied builder
owns provider validation settings; enable ValidateOnBuild and ValidateScopes for the default provider.
A factory may return the same definition already composed with adapters applied for that exact profile. Independent profile and
cleanup failures are reported together. Lifecycle code never runs, but callbacks, Build, options and mapping
can have effects: use inert overrides and isolation. Factory bodies and arbitrary services are not probed.

Provenance covers synchronous mutations through the supplied IServiceCollection; do not retain it.
Suppressed TryAdd attempts and external mutations cannot be attributed to contributors. Service duplicates
are advisory unless explicitly exclusive. Known hosted duplicates, foreign removals and duplicate endpoint
names are errors. Route overlap diagnostics are bounded candidates considering order, precedence, methods,
hosts and constraints; custom policies need HTTP tests. ASP0022/ASP0023 remain enabled.

Named options, live topology changes, Corfedo/Wolverine adapters and Native AOT guarantees are outside this
alpha. gRPC/MCP ownership has no executable package tests here. See [LICENSE](LICENSE) and [NOTICE](NOTICE)
for Apache-2.0 terms and the reservation of Corfedo names, logos and brand assets.

The internal lifecycle decisions and verification scope are in [the design note](docs/designs/composition-lifecycle.md).
