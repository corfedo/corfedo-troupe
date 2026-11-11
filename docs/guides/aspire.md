# Running Troupe profiles with Aspire

`Corfedo.Troupe.Aspire.Hosting` runs one Troupe executable as several Aspire project resources, one per profile or
role set. Reference it from the AppHost project only.

```sh
dotnet add package Corfedo.Troupe.Aspire.Hosting
```

```csharp
var gateway = builder.AddTroupeProject<Projects.Operations>("gateway").WithTroupeProfile("gateway");
var api = builder.AddTroupeProject<Projects.Operations>("api")
    .WithTroupeProfile("api")
    .WithEnvironment("Roles__Remote__gateway", gateway.GetEndpoint("internal"));
```

`AddTroupeProject` adds the project without its launch profile and without the endpoints Aspire derives from its
Kestrel configuration, because Kestrel serves only the listeners the composition requests. The launch profile's
environment variables and arguments are not applied either. In run mode the resource gets the AppHost's environment
name as `DOTNET_ENVIRONMENT`.

`WithTroupeProfile(name)` sets the `Profile` environment variable, `WithTroupeRoles(...)` sets `Roles`. An application
that selects through its own command line or reads only prefixed variables sets them itself:

```csharp
builder.AddTroupeProject<Projects.Host>("host-api").WithArgs("serve", "--profile", "api");
```

`WithTroupe()` marks a resource added with `AddProject`. Its HTTP endpoints that are not listeners fail the AppHost.

## What happens at startup

Before any resource starts, each marked project runs once in [describe mode](manifest.md#describing-a-process) with the
resource's arguments and environment. This takes about half a second per project, and the projects describe
themselves in parallel. Then:

- Each listener the process requests (`KestrelEndpoint(name)`) gets an HTTP endpoint, and
  `Kestrel__Endpoints__<name>__Url` is set to the endpoint's target port. An endpoint the AppHost declares with that
  name is kept, so HTTPS, fixed ports and proxying stay the AppHost's choice.
- The errors `AddTroupe` reports for every resource fail the AppHost once. Nothing starts. Checks that run when the
  host is built (unapplied contributions, service validation) are reported by the started process.
- The dashboard shows each resource's profile, roles and listeners, and the project's roles that no resource runs.
  Leaving a role out is allowed.

```text
Troupe resources cannot start:
  api: needs Workload__Identities__api (read by ApiCalls because AiHost and GatewayHost are not in this process). No Troupe resource runs AiHost.
       GatewayHost runs in "gateway" (listeners: internal).
  ops-bad: TRP013: Unknown profile gatewy. Declared profiles: combined, api, gateway, ai, api-gateway.
  plain: endpoint "http" is not a listener of this composition (listeners: public), so nothing serves it. Aspire adds endpoints from the project's launch profile and Kestrel configuration: add the resource with AddTroupeProject, or remove the endpoint.
```

Sections are shown as environment variable names. A section needed because a component is not in the process names
the resources that run that component. What value to set is the application's decision.

## Values known at run time

Literal argument and environment values pass to the description unchanged. Endpoint references, expressions,
parameters and connection strings are known only after resources start, so they are passed as
`http://troupe-describe.invalid` and count as present.

- Select the profile or roles with literal values. A selection from a parameter is reported as an unknown profile
  named `<run-time value>`.
- The resource's argument and environment callbacks run for the description, before anything is allocated, and again
  when Aspire starts the resource. Pass an endpoint as a reference. A callback that reads an allocated value fails
  the AppHost with that reason.

```csharp
api.WithEnvironment("Upstream", gateway.GetEndpoint("internal"));                              // passes
api.WithEnvironment(context => context.EnvironmentVariables["Upstream"] = gateway.GetEndpoint("internal").Url);   // fails
```

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
builder.AddTroupeProject<Projects.Operations>("gateway")
    .WithHttpsEndpoint(name: "internal").WithoutHttpsCertificate()
    .WithTroupeProfile("gateway");
```

## Publishing

`BeforeStartEvent` also runs when the AppHost publishes, so the same checks run and the manifest gets the same
bindings. The Kestrel URLs are `http://*:{port}`, so a deployed container accepts connections from other hosts.

The [Aspire example](../../examples/Aspire) runs the Operations example as one combined process or as `api`,
`gateway` and `ai` processes.
