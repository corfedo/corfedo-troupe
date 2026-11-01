using Corfedo.Troupe.AspNetCore;

namespace Corfedo.Troupe.Examples.Operations;

public sealed class OperationsComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition)
    {
        definition
            .Profile("combined", p => p.Role<AiRole>().Role<ApiRole>().Role<GatewayRole>())
            .Profile("api", p => p.Role<ApiRole>()).Profile("gateway", p => p.Role<GatewayRole>()).Profile("ai", p => p.Role<AiRole>())
            .Profile("api-gateway", p => p.Role<ApiRole>().Role<GatewayRole>())
            .Rule(new CallsRule());
        definition.Stages("routing", "internal");
    }
}

[Role("gateway")]
public sealed class GatewayRole : IRole
{
    public void Compose(IRoleBuilder role) => role.Requires<GatewayHost>();
}

[Role("ai")]
public sealed class AiRole : IRole
{
    public void Compose(IRoleBuilder role) => role.Requires<AiHost>();
}

/// <summary>The public workload. It calls Gateway and AI through its keyed gateway, wherever they run.</summary>
[Role("api")]
public sealed class ApiRole : IRole
{
    public void Compose(IRoleBuilder role)
    {
        role.Requires<PublicIngress>().Requires<ApiCalls>();
        role.Endpoints(endpoints =>
        {
            endpoints.MapGet("/workspaces/{id}", async (string id, [FromKeyedServices(Workload.Api)] IGateway gateway, CancellationToken cancellationToken) =>
            {
                var workspace = await gateway.SendAsync(new GetWorkspace(id), cancellationToken);
                var summary = await gateway.SendAsync(new Summarize($"{workspace.Name} holds the example's documents"), cancellationToken);
                return new WorkspaceView(workspace.Id, workspace.Name, summary.Text, summary.Words);
            });
            endpoints.MapGet("/routes", ([FromKeyedServices(Workload.Api)] IGateway gateway) => gateway.Routes);
        });
    }
}

public sealed record WorkspaceView(string Id, string Name, string Summary, int Words);

/// <summary>The API workload's destinations.</summary>
public sealed class ApiCalls : IComponent
{
    public void Compose(IComponentBuilder component) => Calls.Declare(component, Workload.Api, d => d
        .To<GatewayHost>(Workload.Gateway)
        .To<AiHost>(Workload.Ai));
}

public sealed class PublicIngress : IComponent
{
    public void Compose(IComponentBuilder component) { component.Requires<Routing>(); component.KestrelEndpoint("public"); }
}

/// <summary>Root routing, shared by the public and internal listeners.</summary>
public sealed class Routing : IComponent
{
    public void Compose(IComponentBuilder component) => component.Use("routing", application => application.UseRouting());
}
