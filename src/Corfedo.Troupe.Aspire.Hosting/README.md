# Corfedo.Troupe.Aspire.Hosting

Runs [Corfedo Troupe](https://github.com/corfedo/corfedo-troupe) profiles and roles as Aspire project resources. Reference
it from the AppHost project only; the application keeps referencing `Corfedo.Troupe`.

```sh
dotnet add package Corfedo.Troupe.Aspire.Hosting
```

```csharp
var gateway = builder.AddTroupeProject<Projects.Operations>("gateway").WithTroupeProfile("gateway");
var api = builder.AddTroupeProject<Projects.Operations>("api")
    .WithTroupeProfile("api")
    .WithEnvironment("Roles__Remote__gateway", gateway.GetEndpoint("internal"));

// An application that selects through its own command line:
builder.AddTroupeProject<Projects.Host>("host-api").WithArgs("serve", "--profile", "api");
```

`AddTroupeProject` adds the project without its launch profile and Kestrel-derived endpoints, because Kestrel serves
only the listeners the composition requests. `WithTroupeProfile` and `WithTroupeRoles` set the `Profile` or `Roles`
environment variable. Before any resource starts, each project describes the composition it would serve with the
resource's arguments and environment (Troupe's describe mode, about half a second per project). Then the package:

- adds an HTTP endpoint for each listener the project requests, unless the AppHost declares an endpoint with that name,
  and sets `Kestrel__Endpoints__<name>__Url` to the endpoint's target port;
- fails the AppHost once, before any resource starts, with the errors `AddTroupe` reports for every resource;
- shows each resource's profile, roles and listeners on the dashboard, and the roles of the project that no resource runs.

```text
Troupe resources cannot start:
  api: needs Roles__Remote__ai (read by ApiCalls because AiHost is not in this process).
       AiHost runs in "ai" (listeners: internal).
  ops-bad: TRP013: Unknown profile gatewy. Declared profiles: combined, api, gateway, ai, api-gateway.
```

The project must be built and must call `AddTroupe` from a Corfedo.Troupe version with describe mode. See the
[Aspire guide](https://github.com/corfedo/corfedo-troupe/blob/main/docs/guides/aspire.md).
