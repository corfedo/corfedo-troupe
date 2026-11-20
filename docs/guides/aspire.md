# Running Troupe profiles with Aspire

A Troupe application is one executable that runs as one process or as several, depending on the profile each process
selects. `Corfedo.Troupe.Aspire.Hosting` lets an Aspire AppHost run that executable as several resources, one per
profile, so the topology you deploy runs on your machine with one command.

## Why use it

Without the package, every process of a split needs its own launch profile, a port for each listener and the settings
it needs to reach the others, and a missing setting shows up as a failure in one of several consoles. With it:

- **One project, several resources.** Each resource names its profile. Nothing else differs between them.
- **Listeners become endpoints.** Each listener the process requests gets an Aspire endpoint and a port.
- **Wiring is checked before anything starts.** Every resource describes its composition first. Settings a process
  needs because a role it calls runs elsewhere are reported for all resources at once, with the resource that runs
  that role.
- **The dashboard shows the composition.** Each resource shows its profile, roles and listeners, and the roles that no
  resource runs.

## Install

Reference the package from the AppHost project only. The application keeps referencing `Corfedo.Troupe`.

```sh
dotnet add package Corfedo.Troupe.Aspire.Hosting
```

## Start with one process

The examples use the [Operations example](../../examples/Operations), which declares the profiles `combined`, `api`,
`gateway`, `ai` and `api-gateway`. The snippets leave out what the full [Aspire example](../../examples/Aspire) adds
for the example's own certificates.

```csharp
var builder = DistributedApplication.CreateBuilder(args);
builder.AddTroupeProject<Projects.Operations>("operations").WithTroupeProfile("combined");
await builder.Build().RunAsync();
```

`AddTroupeProject` adds the project as a resource, and `WithTroupeProfile` selects the profile. The process requests
the listeners `public` and `internal`, so the resource gets endpoints with those names, and the dashboard shows the
profile, its roles and the listeners.

## Split into processes

Run the same project three times, one resource per profile:

```csharp
var gateway = builder.AddTroupeProject<Projects.Operations>("gateway").WithTroupeProfile("gateway");
var ai = builder.AddTroupeProject<Projects.Operations>("ai").WithTroupeProfile("ai");
var api = builder.AddTroupeProject<Projects.Operations>("api").WithTroupeProfile("api");
```

`api` calls operations that the `gateway` and `ai` roles own. In one process those calls stay in memory. Split, `api`
needs their addresses and its client certificate, so the AppHost stops before starting anything:

```text
Troupe resources cannot start:
  api: needs Roles__Remote__ai (read by ApiCalls because AiHost is not in this process).
       AiHost runs in "ai" (listeners: internal).
  api: needs Roles__Remote__gateway (read by ApiCalls because GatewayHost is not in this process).
       GatewayHost runs in "gateway" (listeners: internal).
  api: needs Workload__Identities__api (read by ApiCalls because AiHost and GatewayHost are not in this process).
       AiHost runs in "ai" (listeners: internal).
       GatewayHost runs in "gateway" (listeners: internal).
  api: needs Workload__Root (read by ApiCalls because AiHost and GatewayHost are not in this process).
       AiHost runs in "ai" (listeners: internal).
       GatewayHost runs in "gateway" (listeners: internal).
```

Each entry is a configuration section, written as an environment variable, that a component reads only when another
component is not in its process. The section names and what goes in them belong to the application. Troupe only knows
that they are read and which resource runs the missing component. In the Operations example, `Roles:Remote:<role>`
is the address of the process that runs the role, so it gets that resource's endpoint. The `Workload` sections are the
client certificate paths, so they get literal values:

```csharp
var api = builder.AddTroupeProject<Projects.Operations>("api").WithTroupeProfile("api")
    .WithEnvironment("Roles__Remote__gateway", gateway.GetEndpoint("internal"))
    .WithEnvironment("Roles__Remote__ai", ai.GetEndpoint("internal"))
    .WithEnvironment("Workload__Identities__api__Certificate", "certificates/api.pem")
    .WithEnvironment("Workload__Identities__api__Key", "certificates/api.key")
    .WithEnvironment("Workload__Root", "certificates/root.pem");
```

The Aspire example is this AppHost in full. It also writes the certificates before the other
resources start and declares the `internal` listener for mutual TLS (see [TLS listeners](#tls-listeners)). It runs
either topology:

```sh
aspire run --apphost examples/Aspire/AppHost.csproj                       # one process
aspire run --apphost examples/Aspire/AppHost.csproj -- --Topology split   # api, gateway and ai
```

## Reference

### Adding resources

`AddTroupeProject` adds the project without its launch profile and without the endpoints Aspire derives from its
Kestrel configuration, because Kestrel serves only the listeners the composition requests. Set what the process needs
with `WithEnvironment` and `WithArgs`. In run mode the resource gets the AppHost's environment name as
`DOTNET_ENVIRONMENT`.

`WithTroupe()` marks a resource added with `AddProject`. Its HTTP endpoints that are not listeners fail the AppHost.

### Selecting the profile or roles

`WithTroupeProfile(name)` sets the `Profile` environment variable, and `WithTroupeRoles(...)` sets `Roles`. An
application that selects through its own command line or reads only prefixed variables passes its own arguments:

```csharp
builder.AddTroupeProject<Projects.Host>("host-api").WithArgs("serve", "--profile", "api");
```

Select with literal values. A profile from a parameter is not known when the resource is described and is reported as
an unknown profile named `<run-time value>`.

### Listeners and endpoints

Each listener the process requests with `KestrelEndpoint(name)` gets an HTTP endpoint with that name, and
`Kestrel__Endpoints__<name>__Url` is set to the endpoint's target port. An endpoint the AppHost declares with that name
is kept, so HTTPS, fixed ports and proxying stay the AppHost's choice. Roles that no resource runs are allowed and are
shown on the dashboard.

### TLS listeners

Declare a TLS listener as an HTTPS endpoint, and the package sets an `https://` URL for it. Aspire configures its
developer certificate for HTTPS endpoints. An application with its own certificates opts out with
`WithoutHttpsCertificate()`:

```csharp
builder.AddTroupeProject<Projects.Operations>("gateway")
    .WithHttpsEndpoint(name: "internal").WithoutHttpsCertificate()
    .WithTroupeProfile("gateway");
```

### Endpoints and other values known at run time

Pass an endpoint as a reference, such as `gateway.GetEndpoint("internal")`. The resource's argument and environment
callbacks also run before anything is allocated, so a callback that reads an allocated value fails the AppHost:

```csharp
api.WithEnvironment("Upstream", gateway.GetEndpoint("internal"));                              // passes
api.WithEnvironment(context => context.EnvironmentVariables["Upstream"] = gateway.GetEndpoint("internal").Url);   // fails
```

### Publishing

The same checks run when the AppHost publishes, and the published resources get the same endpoints and Kestrel URLs.
The URLs are `http://*:{port}`, so a deployed container accepts connections from other hosts.

### Requirements

- The project calls `AddTroupe` from a Corfedo.Troupe version with [describe mode](manifest.md#describing-a-process)
  and reaches it without waiting on external services.
- The project is built. Building the AppHost builds its project references.

## How it works

Before any resource starts, each Troupe resource's project runs once in
[describe mode](manifest.md#describing-a-process) with `dotnet run --no-build`, in the AppHost's build configuration,
with the resource's arguments and environment. It writes the composition it would serve and exits. This takes about
half a second per project, and the projects describe themselves in parallel. A project that does not describe itself
within 60 seconds fails the AppHost with that reason.

Literal argument and environment values pass to the description unchanged. Endpoint references, expressions,
parameters and connection strings are known only after resources start, so they are passed as
`http://troupe-describe.invalid` and count as present.

The package then adds the listener endpoints and checks every resource. The errors `AddTroupe` reports for every
resource fail the AppHost once, as one report, and nothing starts. Checks that run when the host is built, such as
unapplied contributions and service validation, are reported by the started process.

The report is the exception's whole text, without a stack trace. An AppHost that ends with
`await builder.Build().RunAsync()` prints it once. `builder.Build().Run()` wraps the exception and prints it twice.
