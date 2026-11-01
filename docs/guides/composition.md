# Components, roles and profiles

## Components and requirements

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
one registration of `T` (TRP004). When several owners register the same service type, Troupe reports the TRP001
advisory; `MultipleAllowed<T>()` declares that this is intended.

## The definition

An `ICompositionDefinition` names what the host composes. `definition.Requires<T>()` requires components directly;
the definition is their owner in the manifest:

```csharp
public sealed class ShopComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition)
    {
        definition.Requires<CatalogComponent>().Requires<OrdersComponent>();
        definition.Stages("routing");
        definition.Rule(new NoDirectDatabaseAccess());
    }
}
```

A definition without roles composes these requirements and reads no selection: it is one application, run with
`builder.AddTroupe<ShopComposition>()`.

## Splitting into processes: roles and profiles

A role is a named set of components that a process can run, named by `[Role]`. The name is what operators type, so it stays stable when the
class is renamed. A profile is an exact supported set of roles, and declaring it registers its roles:

```csharp
definition
    .Profile("orders", profile => profile.Role<OrdersRole>())
    .Profile("notifications", profile => profile.Role<NotificationsRole>())
    .Profile("combined", profile => profile.Role<OrdersRole>().Role<NotificationsRole>());
```

The `Profile` configuration value selects one (`--profile combined`, `PROFILE=combined` or `appsettings.json`).
There is no default: a missing or unknown profile fails with TRP013 and lists the declared ones. For local runs, set
`Profile` in `appsettings.Development.json`. `CompositionExtensions.DeclaredProfiles<T>()` lists the profiles
without composing, for example for usage text.

A definition without profiles registers its roles with `definition.Role<T>()` and is selected by `Roles`, a
comma-separated value such as `--roles orders,notifications`.

In a definition with roles, the components the definition requires are composed in every selection, beside the
selected roles, for parts every process needs such as telemetry.

## Runtime membership

`IComposition` reports what this process composes:

```csharp
var composition = app.Services.GetRequiredService<IComposition>();
if (composition.Includes<GatewayHost>()) { /* call it in process */ } else { /* use the application's transport */ }
```

`Contains<TRole>()` tests a role, `Includes<TComponent>()` a component, and `Profile` and `Roles` name the selection.
The same declarations serve one combined process or several split ones; only the selected profile and its
configuration differ.
