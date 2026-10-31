# Corfedo Troupe

Troupe composes a .NET host from components that declare what they register, what they require, which configuration
they read and what they contribute to other builders. It records the owner of every registration, so a misconfigured
host fails at startup with a diagnostic code that names the owners involved, instead of failing later at runtime.
Roles and profiles select the components of each process, so one executable runs in process or split across
processes from the same declarations.

```sh
dotnet add package Corfedo.Troupe
dotnet add package Corfedo.Troupe.AspNetCore   # middleware stages, endpoints and Kestrel endpoints
dotnet add package Corfedo.Troupe.Testing      # profile verification and test overrides
```

A role requires a component, and the component owns its configuration section:

```csharp
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

The packages depend on each other's exact version, so update them together. See the
[full documentation](https://github.com/corfedo/corfedo-troupe#readme) for the quick start, concepts and examples.

Troupe is licensed under Apache-2.0. Corfedo names, logos and brand assets are reserved; see the NOTICE file.
