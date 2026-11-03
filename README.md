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
  <a href="https://www.nuget.org/packages/Corfedo.Troupe"><img src="https://img.shields.io/nuget/v/Corfedo.Troupe.svg" alt="NuGet"></a>
  <a href="https://github.com/corfedo/corfedo-troupe/actions/workflows/ci.yml"><img src="https://github.com/corfedo/corfedo-troupe/actions/workflows/ci.yml/badge.svg" alt="Tests"></a>
</p>

**Compose a .NET host from components that declare what they need, and catch a misconfigured host before it
serves a request.**

As an application grows, so does the code that composes it. One `Program.cs`, or one `AddEverything()` extension,
wires every project into the host. Projects start to reference each other only because that code needs them, a
change in one area pulls in another it never touches, and the boundaries drift. Nothing says what a new component
should look like, so each one, whether a person or a coding agent adds it, is wired the way the nearest example was.

Troupe gives every part of the host the same shape. A component declares the services it registers, the
configuration it reads, the components it requires and what it contributes to builders the application owns. Troupe
resolves and orders the components, records the owner of every registration, and stops startup with a diagnostic
that names the owners when a section is missing, two components claim one service or a listener is not requested.
When the application later runs as several processes, roles select components from the same declarations.

## Is it for you?

It is a good fit if you have seen any of these:

- The composition code is the largest file in the solution, and every feature edits it.
- Projects reference each other only so that one host can register them, although they work in different areas.
- New components are wired by copying whichever registration looked closest, so no two are wired the same way.
- One component's `AddHttpClient` or `AddDbContext` silently overrides another's.
- A host started fine and failed on its first request because a configuration section was missing.
- To run parts separately you keep a Host project and a `Program.cs` per service, plus one more that runs them all
  together, each repeating the wiring.

## A first application

```sh
dotnet add package Corfedo.Troupe
dotnet add package Corfedo.Troupe.AspNetCore   # middleware stages, endpoints and Kestrel endpoints
dotnet add package Corfedo.Troupe.Testing      # composition verification and test overrides
```

A shop has a catalog and orders. Both need the product list, which owns the `Products` configuration section, and
both serve HTTP through one public listener:

```csharp
public sealed class ShopComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition)
    {
        definition.Requires<CatalogComponent>().Requires<OrdersComponent>();
        definition.Stages("routing");
    }
}

public sealed class CatalogComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<PublicIngressComponent>().Requires<ProductsComponent>();
        component.Endpoints(endpoints => endpoints.MapGet("/products", (ProductCatalog catalog) => catalog.All));
    }
}

public sealed class OrdersComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<PublicIngressComponent>().Requires<ProductsComponent>();
        component.Endpoints(endpoints => endpoints.MapPost("/orders", (OrderRequest order, ProductCatalog catalog) => ...));
    }
}

public sealed class ProductsComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Options<ProductOptions>("Products");
        component.Services(services => services.AddSingleton<ProductCatalog>());
    }
}

public sealed class PublicIngressComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.KestrelEndpoint("public");
        component.Use("routing", application => application.UseRouting());
    }
}
```

`Program.cs` adds the composition and applies its pipeline:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddTroupe<ShopComposition>();
await using var app = builder.Build();
app.UseComposition();
await app.RunAsync();
```

Run it with `dotnet run --project examples/Web`. `ProductsComponent` and `PublicIngressComponent` are composed once,
although two components require them, and each component's registrations and endpoints are recorded with it as their
owner. Without the `Products` section, or without the `public` endpoint in `Kestrel:Endpoints`, the host stops before
it listens:

```text
Unhandled exception. Corfedo.Troupe.CompositionException: TRP005: Corfedo.Troupe.Examples.Web.ProductsComponent: required section Products is missing.
Unhandled exception. Corfedo.Troupe.CompositionException: TRP103: Requested Kestrel endpoint public is missing from Kestrel:Endpoints.
```

One test builds the host with the application's own builder, without starting it:

```csharp
var result = await CompositionVerifier.VerifyAsync<ShopComposition>(() => ShopApplication.CreateBuilder([]));
Assert.True(result.Succeeded, result.ToString());
```

The complete program and its tests are the [Web example](examples/Web) and
[`ApplicationExamplesTests`](tests/Corfedo.Troupe.Examples.Tests/ApplicationExamplesTests.cs).

## Splitting into processes

When parts of the application need to run on their own, roles name sets of components and profiles name the sets of
roles a process runs. The components stay as they are:

```csharp
public void Define(ICompositionDefinitionBuilder definition)
{
    definition
        .Profile("catalog", profile => profile.Role<CatalogRole>())
        .Profile("orders", profile => profile.Role<OrdersRole>())
        .Profile("combined", profile => profile.Role<CatalogRole>().Role<OrdersRole>());
    definition.Stages("routing");
}

[Role("catalog")]
public sealed class CatalogRole : IRole
{
    public void Compose(IRoleBuilder role) => role.Requires<CatalogComponent>();
}
```

One executable then runs with `--profile combined` on a laptop and with `--profile catalog` and `--profile orders`
as separate deployments, instead of one Host project per service. Each process composes only what its roles need;
`CompositionVerifier.VerifyProfilesAsync` builds every profile in one test. See
[components, roles and profiles](docs/guides/composition.md) and the [Messaging](examples/Messaging) and
[Operations](examples/Operations) examples.

## How it works

`AddTroupe` resolves the components the definition requires, and those of the selected roles when it has roles,
runs each component's declarations once in dependency order and checks the configuration they require. `Build` checks the registrations again, including the ones adapters such as `UseWolverine` made after
`AddTroupe`, and builds the service provider with `ValidateOnBuild` and `ValidateScopes` enabled. A failure in either
step throws a `CompositionException` before the host starts.

- **[Components, roles and profiles](docs/guides/composition.md):** requirements, splitting into processes, runtime
  membership (`IComposition.Includes<T>()`), startup checks.
- **[Configuration](docs/guides/configuration.md):** owned options sections, manual reads, sections required only
  when an owner runs in another process.
- **[Contributions](docs/guides/contributions.md):** configuring a builder the application owns, such as
  Wolverine's options, from several components.
- **[The manifest](docs/guides/manifest.md):** what a process composes, who registered each service, who reads
  each section, and application rules.
- **[ASP.NET Core](docs/guides/aspnetcore.md):** middleware stages, endpoints and named Kestrel endpoints.
- **[Testing](docs/guides/testing.md):** verifying the composition and every profile, composing a slice, replacing
  services.
- **[Diagnostics](docs/guides/diagnostics.md):** every `TRP` code and what to change.
- **[Migrating an existing application](docs/guides/migrating.md):** moving `Program.cs` and `AddX()` extensions into
  components one area at a time, and checking whether parts can run separately. The
  [`troupe-migration`](skills/troupe-migration/SKILL.md) skill takes a coding agent through the same steps.

Troupe composes hosts; it does not call between them. Routing a call to another process, its transport,
serialization and caller identity belong to the application, which uses `IComposition.Includes<T>()` to decide
whether an owner runs in this process. The [Operations example](examples/Operations) shows one way to do that.

## Examples

| Example | Shows |
| --- | --- |
| [Web](examples/Web) | Components sharing a component and its configuration in one web application |
| [Worker](examples/Worker) | A generic host with validated options and a `BackgroundService` |
| [Messaging](examples/Messaging) | Roles and profiles: Wolverine handlers contributed per role, local queues and a PostgreSQL transport across processes |
| [Operations](examples/Operations) | Owners called in process or over mutual TLS from the same declarations |

`tests/Corfedo.Troupe.Examples.Tests` verifies every example and starts real hosts. Run all tests with
`dotnet test Corfedo.Troupe.slnx`.

## Status and license

This is version 0.2.0; the API may change before 1.0. Named options, live topology changes, Native AOT and
third-party DI containers are not supported yet. Contributions are welcome through issues and pull requests.

Troupe is licensed under [Apache-2.0](LICENSE). See [NOTICE](NOTICE) for the reservation of Corfedo names, logos and
brand assets.

## Part of Corfedo

The [Corfedo](https://github.com/corfedo) platform composes its roles with Troupe. Corfedo provides code search and
dependency security across all your repositories, for developers and AI coding agents.
