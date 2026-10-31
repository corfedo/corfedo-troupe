# Corfedo Troupe

Troupe is a .NET 10 alpha framework for selecting application roles from one executable.
Roles declare services and shared components. Troupe resolves only the selected closure,
composes dependencies once, and records who contributed each registration.

| Package | Implements |
| --- | --- |
| `Corfedo.Troupe` | Roles, components, strict profiles, graph ordering, DI provenance, options, builder contributions and startup checks |
| `Corfedo.Troupe.AspNetCore` | Application-declared middleware stages, endpoint owner groups and named Kestrel requirements |
| `Corfedo.Troupe.Testing` | Real generic/web profile verification, explicit test overrides and host disposal; no test runner dependency |

## Scope

Troupe composes hosts. It decides what this process contains and checks that composition: selected
roles and components, their services, options, configuration requirements, pipeline and listeners. It
exposes facts about the result, such as `IComposition.Includes<TComponent>()`.

Troupe does not implement operations. Calls between roles, routing to another process, transports such as
gRPC, serialization, caller identity and addresses belong to the application, which builds them on these
facts. Troupe knows only what is composed in this process; "remote" means "not composed here".

Install the packages from NuGet:

```sh
dotnet add package Corfedo.Troupe --prerelease
dotnet add package Corfedo.Troupe.AspNetCore --prerelease
dotnet add package Corfedo.Troupe.Testing --prerelease
```

Troupe is an alpha: APIs change between alpha versions. The three packages depend on each other's exact
version, so update them together.

Build the repository with SDK 10.0.300:

```sh
dotnet build Corfedo.Troupe.slnx
dotnet test Corfedo.Troupe.slnx
dotnet run --project examples/Worker -- --profile worker
dotnet run --project examples/Web -- --profile combined
dotnet run --project examples/Messaging -- --profile combined
dotnet run --project examples/Operations -- certificates
```

The [worker](examples/Worker/Program.cs) integrates Microsoft.Extensions.Hosting, validated options
and BackgroundService. The [web example](examples/Web/Program.cs) integrates ASP.NET Core routing,
Minimal APIs, health checks and named Kestrel configuration at `http://127.0.0.1:5080`. The
[Wolverine messaging example](examples/Messaging/README.md) demonstrates real builder contributions,
selectable consumers, request/reply, local queues, PostgreSQL transport and static handler generation.
The durable mode includes separate publisher/consumer processes and explicit schema provisioning.
Its default address is `http://127.0.0.1:5081`. The [operations example](examples/Operations/README.md)
follows the platform's local/remote design: owner hosts, an internal mutual-TLS listener and callers that
reach each owner in process or remotely, built on `Includes`, `ReadsConfigurationUnless` and
contributor-owned Kestrel endpoints. Its transport is application code.

`Corfedo.Troupe.Examples.Tests` verifies the examples' actual definitions and starts real hosts for
heartbeat, HTTP and message-handling tests. Wolverine dependencies are confined to the messaging
example and its tests.

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

A definition that declares profiles is selected by profile name: `Profile` is an IConfiguration value
(`--profile worker` or `PROFILE=worker`). Declared profiles are exact supported sets, with no opt-out:
`Roles` is rejected, and an unknown name fails with the declared names. The selected profile is
`IComposition.Profile` and `CompositionManifest.Profile`; `CompositionExtensions.DeclaredProfiles<T>()`
lists them without composing, for example for usage text. A definition without profiles is selected by
`Roles`, a comma-separated value (`--roles worker`); any nonempty known role set is accepted and
`Profile` is rejected. Selection is a set. Dependencies precede dependents; ordinal names break
ready-node ties.
Stateless, parameterless definitions only declare: no I/O or resource acquisition. Recorded callbacks
execute after graph validation. Runtime consumers inject `IComposition` for membership and snapshots.
`Includes<TComponent>()` reports whether a component is composed in this process, available from Build on.
`CompositionManifest` answers the same from any snapshot: `Composes<TComponent>()`, `PathTo<TComponent>()` (the
requirement chain from a role), `Active(serviceType, key)`, `OwnedBy(owner)`, `Reads(section)` and
`ContributorsTo<TTarget>()`.

Keep `ValidateOnBuild` and `ValidateScopes` enabled, as the examples do. The call sequence is
`Compose; FinalizeComposition; Build; UseComposition; Run`. `FinalizeComposition` is required after
adapters and before Build: it captures application registrations and rejects unconsumed contributions.
Pipeline application and startup revalidate the finalized composition and fail with TRP018 when the call
was skipped. Each exact
builder target type is applied once. Declaration builders close when their Compose callbacks return.
A failed composition cannot be reused; callback failures retain their owner, original exception and
failure manifest. Runtime IComposition exposes successfully validated snapshots, separately from the build
session. Completed declaration and execution callbacks are released when their work ends.

`Options<T>(section)` binds one owned section per type and adds required presence validation and
ValidateOnStart. A second declaration is an error. `Options<T,TValidator>` explicitly registers a
DI-constructed validator; additional validators compose through DI. `ReadsConfiguration` records manual
section intent. Required absence fails composition and is also validated at Value resolution and startup.
`ReadsConfigurationUnless<TComponent>(section)` records a read that is required only when the component
is not composed, for example the address of a service this process does not host. Its absence fails
composition, finalization and profile verification with TRP005; the manifest records the condition.
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
or declare them through Troupe. `composition.AspNetCore()` exposes endpoint metadata and listener requirements. Before Build,
`builder.AspNetComposition()` returns the stages and listener requirements from the builder, also after a failed
Compose.

`KestrelEndpoint(name)` requires `Kestrel:Endpoints:<name>`. Missing and unrequested configured names
are errors. `KestrelEndpoint(name, callback)` also makes the contributor the endpoint's callback owner; the
callback runs only when that contributor is composed. The application can instead install customization
after Compose with `builder.ConfigureCompositionEndpoint(name, callback)`. A second callback owner,
contributor or application, is rejected with TRP104 before Kestrel replaces TLS policy. Resolve trust
through `ListenOptions.ApplicationServices`.

When any named endpoint is requested, Troupe configures Kestrel's named endpoint loader from the `Kestrel`
section once, with `reloadOnChange: false`, and attaches callbacks to it. The application does not call
`KestrelServerOptions.Configure`; a call after Compose would replace the loader and its callbacks, so Build
fails. Topology changes require restart; raw Kestrel calls remain untracked. Actual bind conflicts belong
to Kestrel.

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

A component slice composes a test-owned root instead of selected roles:

```csharp
builder.ComposeForTest<AppComposition>(root =>
{
    root.Requires<Storage>();
    root.Services(services => services.AddSingleton<Probe>());
}, name: "storage", withinProfile: "worker");
// Apply application adapters here.
builder.FinalizeComposition();
```

The root `test:<name>` declares like a role (`Requires`, `Services`, `Options`, `Contribute`, `Use`, `Endpoints`).
It takes the definition's stages and rules and ignores its roles and profiles, so the manifest has no profile and
no roles. The root follows every component it requires, so it runs last in each stage and registers after them.
`ComposeForTest(root, stages)` composes without a definition. Composition does not finalize: apply adapters and call
`FinalizeComposition` as production does. With `withinProfile`, a required component that the declared profile
does not compose fails with TRP020.

Tests that build a composed host themselves record their services as test-owned on any composed builder,
before or after `FinalizeComposition`. `builder.TestServices(name, services => ...)` adds registrations
owned by `test:<name>` under the ordinary ownership check: removing another owner's registration fails with
TRP003. `builder.ReplaceForTest<TService>(name, descriptor, key, owner)` replaces the one active registration
in the type and key slot in place, so the manifest links the original to the replacement through `RemovedBy`
and `ReplacedBy`. A missing target, or several registrations that the role or component type `owner` does not
narrow to one, fails with TRP019, so a stale replacement cannot become a silent addition. The verifier's
overrides are also owned by `test:<profile>`; they may remove any owner's registrations. `TestServices(name)` and
`ComposeForTest(name)` with the same name share the owner `test:<name>`. After Build the services are read-only, and
both overrides fail with TRP016.

Provenance covers synchronous mutations through the supplied IServiceCollection; do not retain it.
A contribution owns the registrations its callback adds synchronously to the builder's service collection, including
those made before or after validation or a nested `ApplyContributions` inside the callback. The manifest records the
net change of the callback: a replacement inside it appears as an Add and a Remove, with no `ReplacedBy` link, and a
registration added and removed again leaves no record. When one descriptor instance occurs more than once, a removal
is matched to the acting contributor's own occurrence first. Removing or replacing another owner's registration from
a callback, including an adapter's or the host's, fails with TRP003, also when the callback then throws; check
existing contributions for such removals when upgrading. A contribution that adds a service type the host or adapter
also registers raises the TRP001 duplicate advisory. Registrations an adapter makes outside the callbacks, such as the
library's own services, are application-owned.
Suppressed TryAdd attempts and other external mutations cannot be attributed to contributors. Service duplicates
are advisory unless explicitly exclusive. Known hosted duplicates, foreign removals and duplicate endpoint
names are errors. Route overlap diagnostics are bounded candidates considering order, precedence, methods,
hosts and constraints; custom policies need HTTP tests. ASP0022/ASP0023 remain enabled.

Named options, live topology changes, packaged Corfedo/Wolverine adapters and Native AOT guarantees are outside this
alpha. See [LICENSE](LICENSE) and [NOTICE](NOTICE)
for Apache-2.0 terms and the reservation of Corfedo names, logos and brand assets.

