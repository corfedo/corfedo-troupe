# ASP.NET Core

`Corfedo.Troupe.AspNetCore` lets components declare middleware, endpoints and named Kestrel endpoints.

## Stages and endpoints

The definition declares the stage order with `definition.Stages(...)`. A component places middleware in a stage, and
`UseRouting` goes explicitly into one of them:

```csharp
public sealed class PublicIngressComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.KestrelEndpoint("public");
        component.Use("routing", application => application.UseRouting());
    }
}
```

Roles and components map endpoints with `Endpoints(endpoints => ...)`. After `Build`, `app.UseComposition()` applies
the stages in order, then maps each owner's endpoints, so the application decides where its own middleware goes. A
recorded pipeline that was never applied fails startup with TRP105.

## Kestrel endpoints

`KestrelEndpoint(name)` requires `Kestrel:Endpoints:<name>`. A missing endpoint, and a configured endpoint that no
composed component requests, both fail with TRP103, so a process never listens on a port it does not serve.

`KestrelEndpoint(name, configure)` also makes the component the endpoint's single callback owner, for example for
its TLS and client certificate policy; a second owner fails with TRP104. Troupe configures Kestrel's endpoint loader
from the `Kestrel` section, so the application does not call `KestrelServerOptions.Configure`.

Before `Build`, `builder.AspNetComposition()` returns the declared stages and Kestrel endpoint requirements, also
after a failed `AddTroupe`. Check `Complete` first: it is false when composition failed before every contributor's
declarations ran.

```csharp
if (builder.AspNetComposition() is { Complete: true } web)
    Console.Error.WriteLine($"Configure Kestrel:Endpoints for {string.Join(", ", web.KestrelEndpoints.Select(e => e.Name))}.");
```

A code listener beside named endpoints must use `UseHttps(HttpsConnectionAdapterOptions)`,
`UseHttps(TlsHandshakeCallbackOptions)` or `UseHttps(ServerOptionsSelectionCallback, state)`. The other `UseHttps`
overloads load the endpoint configuration immediately, so an endpoint callback attached later would never run;
Troupe detects this and fails with TRP106.
