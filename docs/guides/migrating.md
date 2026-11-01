# Migrating an existing application

A migration moves the code that composes the host into components, one area at a time, while the application keeps
building and running. Most applications stop there, in one process. Roles and profiles come later, and only when
parts of the application must run on their own.

## Before you start

- `AddTroupe` extends `IHostApplicationBuilder`: `WebApplication.CreateBuilder` or `Host.CreateApplicationBuilder`.
  An application on `Host.CreateDefaultBuilder` with a `Startup` class moves to that hosting model first.
- Troupe builds the service provider with `ValidateOnBuild` and `ValidateScopes`. Remove `ConfigureContainer`,
  `Host.UseServiceProviderFactory` and `Host.UseDefaultServiceProvider` (TRP018). A captive scoped dependency that
  never failed before now fails `Build`.
- Third-party containers such as Autofac, Native AOT and named options through `Options<T>` are not supported.
- Packages: `Corfedo.Troupe` in every project that holds components and in the Host, `Corfedo.Troupe.AspNetCore` where
  components declare middleware, endpoints or listeners, and `Corfedo.Troupe.Testing` in the test project.
- Have tests that start the application and send a request to each area before you begin, so that a failure during
  the migration can be told apart from one that was already there.

## The application before

Two areas, each with an extension method in its own project, wired by `Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCatalog(builder.Configuration);
builder.Services.AddOrders(builder.Configuration);
var app = builder.Build();
app.UseRouting();
app.MapCatalog();
app.MapOrders();
app.Run();

public static class CatalogRegistration
{
    public static IServiceCollection AddCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ProductOptions>(configuration.GetSection("Products"));
        services.AddSingleton<ProductCatalog>();
        return services;
    }

    public static IEndpointRouteBuilder MapCatalog(this IEndpointRouteBuilder endpoints) { ... }
}

public static class OrdersRegistration
{
    // Orders prices with the product list, so it registers the catalog too.
    public static IServiceCollection AddOrders(this IServiceCollection services, IConfiguration configuration) =>
        services.AddCatalog(configuration);

    public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder endpoints) { ... }
}
```

The Orders project references the Catalog project only to call `AddCatalog`, `ProductCatalog` is registered twice,
and without a `Products` section the host starts and serves an empty list.

## 1. Find the areas

List every place that registers services, binds configuration or adds middleware and endpoints: `Program.cs`,
extension methods on `IServiceCollection`, `IHostApplicationBuilder`, `IEndpointRouteBuilder` and
`IApplicationBuilder`, the entries in `Kestrel:Endpoints`, and every Host project of the solution. Group what you find
by the area that owns it. A part that several areas need, such as the product list above, becomes its own component.

## 2. Add the composition

Move builder creation into a method tests can call, add a definition, and migrate the first area. Configuration
sources go before `AddTroupe`, which checks the sections components require (TRP005). Endpoints that `Program.cs`
still maps go before `app.UseComposition()`, which records the endpoints for Troupe's route checks:

```csharp
var app = ShopApplication.CreateBuilder(args).Build();
app.MapOrders();                                     // not migrated yet
app.UseComposition();
app.Run();

public static class ShopApplication
{
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddTroupe<ShopComposition>();
        return builder;
    }
}

public sealed class ShopComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition)
    {
        definition.Requires<CatalogComponent>();
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
    public void Compose(IComponentBuilder component) => component.Use("routing", application => application.UseRouting());
}
```

`ProductsComponent` is its own component because Catalog and Orders both need it; it is composed once however many
components require it.

Registrations that `Program.cs` still makes are owned by `application` in the [manifest](manifest.md). `AddOrders`
only called `AddCatalog`, so its call goes in the same change. Left in place, TRP001 would report `ProductCatalog` from
both `ProductsComponent` and `application`, and the product list would be bound twice, so `/products` would list every
product twice.

### Listeners

Once the composition declares stages, middleware or endpoints, Troupe checks the listeners: every entry in
`Kestrel:Endpoints` must be requested by a composed component with `KestrelEndpoint(name)`, and every requested name
must be configured (TRP103). An application with entries in `Kestrel:Endpoints` requests them in this first change,
usually from its ingress component. An application without entries keeps `--urls` and `ASPNETCORE_URLS`. The
[Web example](../../examples/Web) requests a `public` endpoint.

## 3. Move one area at a time

Move an area's registrations, the sections it reads, its middleware and its endpoints into its component, and delete
them from `Program.cs` and the old extension methods in the same change. Leftovers are not reported reliably: a
component's `TryAdd` silently yields to a registration `Program.cs` made first, a registration after `AddTroupe` only
raises the TRP001 advisory, a second options binding is not reported at all, and an application `RemoveAll` of a
component's service passes.

```csharp
public sealed class OrdersComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<PublicIngressComponent>().Requires<ProductsComponent>();
        component.Endpoints(endpoints => endpoints.MapPost("/orders", (OrderRequest order, ProductCatalog catalog) => ...));
    }
}
```

With Orders migrated, `definition.Requires<CatalogComponent>().Requires<OrdersComponent>()`, and `AddOrders`,
`MapOrders` and their calls are gone. A missing `Products` section now fails before the host listens. The finished
application is the [Web example](../../examples/Web).

`Requires<T>()` names the component class, so the requiring project references the required one. In a layered
solution where Infrastructure implements interfaces that Application defines, the Application component cannot
require the Infrastructure component; the definition requires both, and `Build` still fails when a service is missing.

Components run in dependency order, and among the components whose requirements have run, in the ordinal order of
their full type names, not in the order `Program.cs` used. `Services` callbacks run inside `AddTroupe`; contributions
run later, when `Program.cs` applies them. Keep registrations whose order matters, such as EF Core interceptors or a
mediator's pipeline behaviors, in one callback, or in callbacks of one kind with the later component requiring the
earlier.

| Before | In a component |
| --- | --- |
| `services.Configure<T>(configuration.GetSection("X"))` | `Options<T>("X")`, or `Options<T, TValidator>` |
| `configuration["X"]` or `GetConnectionString` at registration | `ReadsConfiguration("X")` and read `IConfiguration` in a factory, such as `AddDbContext<T>((provider, options) => ...)` |
| `AddOptions<T>("name").BindConfiguration(...)` | the same call in `Services`, with `ReadsConfiguration` |
| `if (configuration["Storage:Kind"] == ...)` choosing a registration | register the candidates and choose in a factory. A candidate with its own hosted services, listeners or required sections is a component of its own: the definition or a profile chooses components, configuration does not |
| `services.AddX()` of one area | `Services(services => services.AddX())` |
| `AddX()` of another area called for its services | `Requires<XComponent>()` |
| `app.UseX()` | `Use(stage, application => application.UseX())`, with the stage in `definition.Stages(...)` |
| `app.MapX()` | `Endpoints(endpoints => endpoints.MapX())` |
| Hosted services, timers | `Services(services => services.AddHostedService<T>())` |
| Startup checks before serving | `Check((services, cancellationToken) => ...)`; checks run after hosted services are constructed, so work that a hosted service's constructor depends on stays between `Build` and `Run` |
| Builders the application owns: Wolverine, MassTransit | `Contribute<T>(...)`, applied once in `Program.cs` ([contributions](contributions.md)) |

`UseRouting` goes into a stage. Middleware that must run before routing, such as an exception handler, stays in
`Program.cs` before `app.UseComposition()`. Middleware that reads the selected endpoint, such as authorization or a
policy on endpoint metadata, goes into a stage after the routing stage, or stays in `Program.cs` after
`app.UseComposition()`.

### Extensions on the host builder

Some libraries, such as .NET Aspire client integrations, extend `IHostApplicationBuilder` instead of
`IServiceCollection`. A component
contributes to the builder, and `Program.cs` applies the contributions with an explicit type argument; without it, the
inferred `WebApplicationBuilder` leaves them unapplied (TRP008):

```csharp
component.ReadsConfiguration("ConnectionStrings:cache");
component.Contribute<IHostApplicationBuilder>(host => host.AddRedisClient("cache"));

// In ShopApplication.CreateBuilder, after AddTroupe
builder.ApplyContributions<IHostApplicationBuilder>(builder);
```

The registrations made in the callback are owned by the component. Configuration sources added there come after
`AddTroupe` checked the sections, so add them before `AddTroupe` instead.

### Controllers and scanned endpoints

MVC discovers controllers from application parts, by default every controller in the entry assembly, whatever the
composition contains. One component owns `AddControllers()` and maps them once with `MapControllers()`; mapping them
from two components maps every controller twice (TRP102). Endpoint groups found by reflection over an assembly behave
the same way. In one process this changes nothing; see [splitting](#5-split-into-processes-optional) for roles.

## 4. Verify

One test builds the host without starting it and checks the composition. In a test project, pass the content root
and environment the application needs, as the examples do:

```csharp
var result = await CompositionVerifier.VerifyAsync<ShopComposition>(() => ShopApplication.CreateBuilder([]));
Assert.True(result.Succeeded, result.ToString());
```

The verifier does not start the host, so startup checks and hosted services do not run, and middleware that
`Program.cs` adds is not part of it. Keep the tests that start the application, for example with
`WebApplicationFactory<Program>`, and send a request to each migrated area.

Read the advisories in `IComposition.Manifest.Diagnostics` as well: TRP001 lists service slots that several owners
register, such as two components calling `AddHttpClient<T>` or `AddDbContext<T>` for one type. Keep one owner, or
declare `MultipleAllowed<T>()` where several are intended. The migration is complete when the registrations
`Program.cs` owns are only the host's defaults and those of the adapters it applies.

Tools that start the application to find its host, such as `dotnet ef`, run `Build`, and with it the composition
checks. Give them the configuration the components require, or an `IDesignTimeDbContextFactory<T>`.

## 5. Split into processes (optional)

When parts of the application must run on their own, roles name sets of the existing components and profiles name
the sets of roles a process runs ([composition](composition.md)). Before adding them, check that each part can run
without the others:

- **State in memory:** singletons, caches, static fields and in-process queues that two parts share stop being shared.
- **Calls and events:** a direct call or an in-process event (a mediator notification, a local queue) from one part to
  another needs a contract when the other part runs elsewhere, such as a durable message or a remote call.
  `IComposition.Includes<T>()` and `ReadsConfigurationUnless<T>` choose between in-process and remote.
- **One instance:** background jobs, timers and startup migrations that assume one process run once per process.
- **Machine resources:** local files, locks and in-memory data protection keys, sessions, rate limits and SignalR
  connections work only within one process.
- **Transactions:** an operation whose transaction writes the data of two parts must run where it can write both;
  if the split puts those writes in different processes, the parts stay together or the operation changes.
- **Discovery:** controllers and scanned endpoints of every part in one assembly are mapped in every process. Move each
  part's controllers into its own assembly; the controllers component clears the application parts and each area's
  component adds its own with `AddApplicationPart`.

Build the contracts a boundary needs before its parts run as separate processes. `CompositionVerifier.VerifyProfilesAsync`
builds every profile. It finds a constructor dependency on a component the profile does not compose, but not one hidden
in a service factory, an empty `IEnumerable<T>` or a minimal API parameter that becomes a request body. One profile that
runs every role, such as `combined`, keeps the single-process deployment.

Merging several Host projects into one executable also merges what each host brings: its `appsettings*.json`, user
secrets ID, application name and content root, launch settings, Dockerfiles and orchestration references. Decide each
before the Host projects go.

## With a coding agent

The repository's [`skills/troupe-migration`](../../skills/troupe-migration/SKILL.md) skill guides a coding agent
through these steps. It reports the inventory and the proposed components before it edits, and it checks whether parts
can run separately before it proposes roles. For Claude Code, copy the directory into `.claude/skills/` in your
repository or into `~/.claude/skills/`. Other agents that read `SKILL.md` skills load the same directory.
