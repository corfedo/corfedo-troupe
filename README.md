<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/corfedo-logo-dark.svg">
    <img src="docs/images/corfedo-logo-light.svg" alt="Corfedo" width="280">
  </picture>
</p>

<h1 align="center">Corfedo Troupe</h1>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache--2.0-blue.svg" alt="License: Apache-2.0"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10-512BD4.svg" alt=".NET 10"></a>
  <a href="https://github.com/orgs/corfedo/packages?repo_name=corfedo-troupe"><img src="https://img.shields.io/github/v/release/corfedo/corfedo-troupe?label=GitHub%20Packages" alt="GitHub Packages"></a>
  <a href="https://github.com/corfedo/corfedo-troupe/actions/workflows/ci.yml"><img src="https://github.com/corfedo/corfedo-troupe/actions/workflows/ci.yml/badge.svg" alt="Tests"></a>
</p>

**A host composition framework for .NET.** In a large application, `Program.cs` grows into hundreds of
registrations nobody owns, and a missing configuration section or a conflicting registration shows up only when a
request hits it. Troupe builds the host from components that declare what they register, what they require, which
configuration they read and what they contribute to other builders. It records the owner of every registration, so a
misconfigured host fails at startup with a diagnostic code that names the owners involved. Roles and profiles select
the components of each process, so one executable runs combined or split across processes from the same
declarations.

Use it to keep a multi-role .NET application composable and checkable:

- **Declared dependencies.** Roles and components require other components; Troupe resolves the selected closure,
  orders it and composes each component once.
- **One executable, many deployments.** Profiles name exact role sets, such as `combined`, `api` or `worker`,
  selected with `--profile` at startup.
- **Startup diagnostics.** Missing configuration sections, foreign service removals, unapplied contributions and
  Kestrel endpoint mismatches fail with stable `TRP` codes.
- **A queryable manifest.** Every process can tell what it composes, who registered each service and who reads each
  configuration section.
- **Real-host tests.** Verify every declared profile, compose a slice of components and replace services with
  recorded test ownership.

> [!NOTE]
> Troupe composes hosts; it does not implement operations. Calls between roles, routing to another process,
> transports such as gRPC, serialization, caller identity and addresses belong to the application, which builds them
> on Troupe's facts. Troupe knows only what is composed in this process: "remote" means "not composed here".

## Quick start

Requires the .NET 10 SDK. The packages are published to GitHub Packages, which requires a GitHub token with
`read:packages` to restore, also for public packages. Add the source once:

```sh
dotnet nuget add source https://nuget.pkg.github.com/corfedo/index.json --name corfedo \
  --username <github-user> --password <token-with-read:packages>
```

```sh
dotnet add package Corfedo.Troupe
dotnet add package Corfedo.Troupe.AspNetCore   # middleware stages, endpoints and Kestrel endpoints
dotnet add package Corfedo.Troupe.Testing      # profile verification and test overrides
```

The packages depend on each other's exact version, so update them together. A role requires a component, and a
definition lists the roles and the profiles that serve them:

```csharp
public sealed class WorkerComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition) => definition.Role<WorkerRole>().Profile("worker", "worker");
}

[Role("worker")]
public sealed class WorkerRole : IRole
{
    public void Compose(IRoleBuilder role)
    {
        role.Requires<WorkerSettings>();
        role.Services(services => services.AddHostedService<Heartbeat>());
    }
}

public sealed class WorkerSettings : IComponent
{
    public void Compose(IComponentBuilder component) => component.Options<HeartbeatOptions, HeartbeatValidator>("Worker");
}
```

The host composes the definition, applies any builder adapters, finalizes and builds:

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
builder.Compose<WorkerComposition>();
builder.FinalizeComposition();
using var host = builder.Build();
await host.RunAsync();
```

Run it with `dotnet run -- --profile worker`. Without a `Worker` section in configuration, `Compose` throws a
`CompositionException` whose TRP005 diagnostic names `WorkerSettings` and the missing section, and the host never
starts. The complete program is the [worker example](examples/Worker).

`FinalizeComposition` goes after adapters and before `Build`: it captures the registrations adapters made and rejects
contributions no adapter applied. Skipping it fails with TRP018. Keep `ValidateOnBuild` and `ValidateScopes` enabled.

## Concepts

### Components and requirements

An `IComponent` is a shared dependency, composed once however many contributors require it. `Requires<T>()` places
`T` before the requiring role or component. `Compose` only records declarations; callbacks such as `Services` run
after the selected graph is validated and ordered, so declarations must not perform I/O or acquire resources.

```csharp
public sealed class OrdersConsumer : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<MessageProcessing>();
        component.Services(services => services.AddSingleton<OrderFormatter>());
    }
}
```

`Check(...)` records a startup check that runs before hosted services start. `ExclusiveService<T>()` allows at most
one registration of `T` (TRP004).

### Options and configuration

`Options<T>(section)` binds one owned section, validates its presence and adds `ValidateOnStart`;
`Options<T, TValidator>` also registers a validator. `ReadsConfiguration` records a section the component reads
itself. A required section that is absent fails composition with TRP005. The manifest records paths and owners,
never values.

```csharp
component.Options<StorageOptions, StorageOptionsValidator>("Storage");
component.ReadsConfiguration("ConnectionStrings:Storage");
component.ReadsConfiguration("Storage:Cache", required: false);
component.ReadsConfigurationUnless<GatewayHost>("Roles:Remote:gateway");
```

`ReadsConfigurationUnless<TComponent>` makes a section required only when `TComponent` is not composed here, such
as the address of an owner that runs in another process.

### Contributions

A component can contribute to a third-party builder that the application owns, such as Wolverine's options. The
application applies all contributions to that builder type once, inside its own configuration call:

```csharp
// In a component
component.Contribute<WolverineOptions>(options => options.Discovery.IncludeType(typeof(PlaceOrderHandler)));

// In the application
builder.UseWolverine(options => builder.ApplyContributions(options));
```

Registrations a contribution callback adds are owned by its contributor. A contribution no adapter applied fails
`FinalizeComposition` with TRP008. When several owners register the same service type, Troupe reports the TRP001
advisory; `MultipleAllowed<T>()` declares that this is intended.

### Roles and profiles

A role is a selectable workload named by `[Role]`. A profile is an exact supported role set:

```csharp
definition.Role<OrdersRole>().Role<NotificationsRole>()
    .Profile("orders", "orders").Profile("notifications", "notifications")
    .Profile("combined", "orders", "notifications");
```

The `Profile` configuration value selects one (`--profile combined` or `PROFILE=combined`); an unknown name fails
with TRP013 and lists the declared profiles. A definition without profiles is selected by `Roles` instead, a
comma-separated value such as `--roles orders,notifications`. `CompositionExtensions.DeclaredProfiles<T>()` lists the
profiles without composing, for example for usage text.

At runtime, `IComposition` reports membership: `composition.Includes<GatewayHost>()` tells a caller whether to call
that owner in process or over the application's own transport. The same declarations serve one combined process or
several split ones; only the selected profile and its configuration differ.

### The composition manifest

`Compose`, `FinalizeComposition` and `IComposition.Manifest` return a `CompositionManifest`: the selected profile and
roles, contributors in execution order, every service registration with its owner, configuration reads,
contributions and diagnostics.

```csharp
var manifest = host.Services.GetRequiredService<IComposition>().Manifest;
bool processing = manifest.Composes<MessageProcessing>();
var path = manifest.PathTo<MessageProcessing>();   // --profile orders: orders, OrdersConsumer, MessageProcessing
var owner = path[^1];
var registrations = manifest.OwnedBy(owner).Where(s => s.Active);
var readers = manifest.Reads("Storage").Select(r => r.Owner);
var wolverine = manifest.ContributorsTo<WolverineOptions>();
```

`Active(serviceType, key)` lists the registrations in one service slot. Removed and replaced registrations stay in
the manifest with `RemovedBy` and `ReplacedBy`. An `ICompositionRule` added with `definition.Rule(...)` evaluates
application policy against the manifest.

### Diagnostics

A failed composition throws `CompositionException`. Its `Diagnostics` carry a stable code, a severity, a message and
the implicated owners; its `Manifest` is the snapshot available at the failure. Errors fail composition; advisories
are reported in `CompositionManifest.Diagnostics` and do not.

| Codes | Family |
| --- | --- |
| TRP001–TRP008 | Services, configuration and contributions: duplicate services (advisory), duplicate hosted services, removing another owner's registration, exclusive slots, missing sections, factory hosted services (advisory), a second `Options<T>`, unapplied contributions |
| TRP010–TRP014 | Definition and selection: role names, profiles, unknown roles, profile selection, dependency cycles |
| TRP015–TRP020 | Phases and tests: declarations after `Compose`, calls in the wrong phase, callback failures, a skipped `FinalizeComposition`, test replacement targets, a test root outside its profile |
| TRP100–TRP106 | ASP.NET Core: stages and routing, endpoint names, route overlaps (advisory), Kestrel endpoints, a second endpoint callback owner, an unapplied pipeline, early Kestrel configuration loads |

Each message names the owner, section, service or endpoint involved.

## ASP.NET Core

`Corfedo.Troupe.AspNetCore` lets components declare middleware, endpoints and named Kestrel endpoints. The
definition declares the stage order with `definition.Stages("routing")`, and a component places `UseRouting`
explicitly in a stage:

```csharp
public sealed class PublicIngress : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.KestrelEndpoint("public");
        component.Use("routing", application => application.UseRouting());
    }
}
```

Roles map endpoints with `role.Endpoints(endpoints => ...)`. After `Build`, `app.UseComposition()` applies the
stages in order, then maps each owner's endpoints; a recorded pipeline that was never applied fails startup with
TRP105. See the [web example](examples/Web/Program.cs) for the complete program.

`KestrelEndpoint(name)` requires `Kestrel:Endpoints:<name>`; a missing endpoint and a configured endpoint that no
composed component requests both fail with TRP103. `KestrelEndpoint(name, configure)` also makes the component the
endpoint's single callback owner, for example for its TLS and client certificate policy; a second owner fails with
TRP104. Troupe configures Kestrel's endpoint loader from the `Kestrel` section, so the application does not call
`KestrelServerOptions.Configure`.

Before `Build`, `builder.AspNetComposition()` returns the declared stages and Kestrel endpoint requirements, also
after a failed `Compose`. Check `Complete` first: it is false when `Compose` failed before every contributor's
declarations ran.

```csharp
if (builder.AspNetComposition() is { Complete: true } web)
    Console.Error.WriteLine($"Configure Kestrel:Endpoints for {string.Join(", ", web.KestrelEndpoints.Select(e => e.Name))}.");
```

A code listener beside named endpoints must use `UseHttps(HttpsConnectionAdapterOptions)`,
`UseHttps(TlsHandshakeCallbackOptions)` or `UseHttps(ServerOptionsSelectionCallback, state)`. The other `UseHttps`
overloads load the endpoint configuration immediately, so an endpoint callback attached later would never run; Troupe
detects this and fails with TRP106.

```csharp
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 5443,
    listen => listen.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificate = certificate })));
```

## Testing

`Corfedo.Troupe.Testing` works with any test runner. `CompositionVerifier` builds every declared profile of a
definition with real builders, resolves declared options, maps web endpoints and disposes each host, without starting
it:

```csharp
var result = await CompositionVerifier.VerifyProfilesAsync<WorkerComposition>(
    profile => WorkerApplication.CreateBuilder(["--profile", profile.Name], contentRoot));
Assert.True(result.Succeeded, result.ToString());
```

The factory returns a fresh builder per profile; an optional `overrides` callback replaces external resources.

`ComposeForTest` composes a slice: a test-owned root `test:<name>` that declares like a role, with the definition's
stages and rules. With `withinProfile`, a required component that the profile does not compose fails with TRP020.

```csharp
builder.ComposeForTest<WorkerComposition>(root => root.Requires<WorkerSettings>(), name: "settings", withinProfile: "worker");
builder.FinalizeComposition();
```

On a composed builder, `TestServices(name, services => ...)` adds registrations owned by `test:<name>`; removing
another owner's registration there fails with TRP003. `ReplaceForTest<TService>(name, descriptor)` replaces the one
active registration in a service slot in place, and the manifest links the original to the replacement. A missing
target, or several that the `owner` argument does not narrow to one, fails with TRP019.

When one owner registers several descriptors in a slot, a predicate selects exactly one of them. A typed client
configured twice for one name has two `IConfigureOptions<HttpClientFactoryOptions>`; this replaces the one that sets
the primary handler:

```csharp
builder.ReplaceForTest<IConfigureOptions<HttpClientFactoryOptions>>("stub-handler",
    ServiceDescriptor.Singleton<IConfigureOptions<HttpClientFactoryOptions>>(new ConfigureNamedOptions<HttpClientFactoryOptions>(
        "catalog", options => options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new StubHandler()))),
    owner: typeof(CatalogComponent),
    match: descriptor =>
    {
        if (descriptor.ImplementationInstance is not ConfigureNamedOptions<HttpClientFactoryOptions> { Name: "catalog" } configure) return false;
        var probe = new HttpClientFactoryOptions();
        configure.Configure("catalog", probe);
        return probe.HttpMessageHandlerBuilderActions.Count > 0;
    });
```

Zero or several matches fail with TRP019, and the message lists the candidates.

## Examples

| Example | Shows |
| --- | --- |
| [Worker](examples/Worker) | A generic host with one role, validated options and a `BackgroundService` |
| [Web](examples/Web) | Two roles sharing an ingress component: stages, Minimal APIs, health checks and a named Kestrel endpoint |
| [Messaging](examples/Messaging) | Wolverine handlers contributed per role, request/reply, local queues and a PostgreSQL transport across processes |
| [Operations](examples/Operations) | Owners called in process or over mutual TLS from the same declarations, using `Includes` and `ReadsConfigurationUnless` |

`tests/Corfedo.Troupe.Examples.Tests` verifies every example profile and starts real hosts; run all tests with
`dotnet test Corfedo.Troupe.slnx`.

## Status and license

This is version 0.1.0; the API may change before 1.0. Named options, live topology changes and Native AOT are not
supported yet. Contributions are welcome through issues and pull requests.

Troupe is licensed under [Apache-2.0](LICENSE). See [NOTICE](NOTICE) for the reservation of Corfedo names, logos and
brand assets.
