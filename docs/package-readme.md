# Corfedo Troupe

**Compose a .NET host from components that declare what they need, and catch a misconfigured host before it
serves a request.** As an application grows, one `Program.cs` wires every project into the host, projects reference
each other only because that code needs them, and each new component is wired the way the nearest example was.
Troupe gives every part of the host the same shape: a component declares the services it registers, the
configuration it reads and the components it requires. Troupe records the owner of every registration and stops
startup with a diagnostic that names the owners. When the application later runs as several processes, roles select
components from the same declarations.

```sh
dotnet add package Corfedo.Troupe
dotnet add package Corfedo.Troupe.AspNetCore   # middleware stages, endpoints and Kestrel endpoints
dotnet add package Corfedo.Troupe.Testing      # composition verification and test overrides
```

The definition requires a component, and the component owns its configuration section:

```csharp
public sealed class WorkerComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition) => definition.Requires<HeartbeatComponent>();
}

public sealed class HeartbeatComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Options<HeartbeatOptions>("Worker");
        component.Services(services => services.AddHostedService<HeartbeatService>());
    }
}
```

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.AddTroupe<WorkerComposition>();
using var host = builder.Build();
await host.RunAsync();
```

Without a `Worker` section, `AddTroupe` throws a `CompositionException` with TRP005, naming `HeartbeatComponent` and the
section, and the host never starts.

The packages depend on each other's exact version, so update them together. See the
[documentation](https://github.com/corfedo/corfedo-troupe#readme) for a first application, splitting into processes,
guides and examples.

Troupe is licensed under Apache-2.0. Corfedo names, logos and brand assets are reserved; see the NOTICE file.
