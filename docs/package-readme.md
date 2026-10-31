# Corfedo Troupe

**A host composition framework for .NET.** In a large application, `Program.cs` grows into hundreds of
registrations nobody owns, and a missing configuration section or a conflicting registration shows up only when a
request hits it. Troupe builds the host from components that declare what they register, what they require, which
configuration they read and what they contribute to other builders. It records the owner of every registration, so a
misconfigured host fails at startup with a diagnostic code that names the owners involved. Roles and profiles select
the components of each process, so one executable runs combined or split across processes from the same
declarations.

The packages are published to GitHub Packages, which requires a GitHub token with `read:packages` to restore. Add
the source once:

```sh
dotnet nuget add source https://nuget.pkg.github.com/corfedo/index.json --name corfedo \
  --username <github-user> --password <token-with-read:packages>
```

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
