# Running Troupe profiles with Aspire

`Corfedo.Troupe.Aspire.Hosting` runs one Troupe executable as several Aspire project resources, one per profile or
role set. Reference it from the AppHost project only.

```sh
dotnet add package Corfedo.Troupe.Aspire.Hosting
```

```csharp
var gateway = builder.AddProject<Projects.Operations>("gateway").WithTroupeProfile("gateway");
var api = builder.AddProject<Projects.Operations>("api")
    .WithTroupeProfile("api")
    .WithEnvironment("Roles__Remote__gateway", gateway.GetEndpoint("internal"));
```

`WithTroupeProfile(name)` sets the `Profile` environment variable, `WithTroupeRoles(...)` sets `Roles`. An application
that selects through its own command line or reads only prefixed variables sets them itself and calls `WithTroupe()`:

```csharp
builder.AddProject<Projects.Host>("host-api").WithArgs("serve", "--profile", "api").WithTroupe();
```

## What happens at startup

Before any resource starts, each marked project runs once in [describe mode](manifest.md#describing-a-process) with the
resource's arguments and environment. This takes about half a second per project, and the projects describe
themselves in parallel. Literal values pass unchanged. Endpoint references, expressions, parameters and connection
strings are known only later, so they are passed as `http://troupe-describe.invalid` and count as present. Then:

- Each listener the process requests (`KestrelEndpoint(name)`) gets an HTTP endpoint, and
  `Kestrel__Endpoints__<name>__Url` is set to the endpoint's target port. An endpoint the AppHost declares with that
  name is kept, so HTTPS, fixed ports and proxying stay the AppHost's choice.
- Every composition error of every resource fails the AppHost once. Nothing starts.
- The dashboard shows each resource's profile, roles and listeners, and the project's roles that no resource runs.
  Leaving a role out is allowed.

```text
Troupe resources cannot start:
  api: needs Workload__Identities__api (read by ApiCalls because AiHost and GatewayHost are not in this process). No Troupe resource runs AiHost.
       GatewayHost runs in resource "gateway" (listeners: internal), for example:
       .WithEnvironment("Workload__Identities__api", gateway.GetEndpoint("internal"))
  ops-bad: TRP013: Unknown profile gatewy. Declared profiles: combined, api, gateway, ai, api-gateway.
```

Sections are shown as environment variable names. A section needed because a component is not in the process names
the resources that run that component. What value to set is still the application's decision.

## Requirements

- The project calls `AddTroupe` from a Corfedo.Troupe version with describe mode and reaches it without waiting on
  external services. A project that does not describe itself within 60 seconds fails the AppHost with that reason.
- The project is built. The description runs it with `dotnet run --no-build` in the AppHost's build configuration;
  building the AppHost builds its project references.

## TLS listeners

Declare a TLS listener as an HTTPS endpoint, and the package sets an `https://` URL for it. Aspire configures its
developer certificate for HTTPS endpoints. An application with its own certificates opts out with
`WithoutHttpsCertificate()`.

```csharp
builder.AddProject<Projects.Operations>("gateway")
    .WithHttpsEndpoint(name: "internal").WithoutHttpsCertificate()
    .WithTroupeProfile("gateway");
```

## Publishing

`BeforeStartEvent` also runs when the AppHost publishes, so the same checks run and the manifest gets the same
bindings. The Kestrel URLs are `http://*:{port}`, so a deployed container accepts connections from other hosts.

The [Aspire example](../../examples/Aspire) runs the Operations example as one combined process or as `api`,
`gateway` and `ai` processes.
