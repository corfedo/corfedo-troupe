using Corfedo.Troupe.AspNetCore;

namespace Corfedo.Troupe.Examples.Web;

public sealed class WebComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition)
    {
        definition.Role<WebRole>().Role<HealthRole>().Profile("web", "web").Profile("combined", "web", "health");
        definition.Stages("routing");
    }
}

[Role("web")]
public sealed class WebRole : IRole
{
    public void Compose(IRoleBuilder role)
    {
        role.Requires<PublicIngress>();
        role.Endpoints(endpoints => endpoints.MapGet("/", (IComposition composition) =>
            new { Roles = composition.Roles.Select(r => r.Name) }).WithName("home"));
    }
}

[Role("health")]
public sealed class HealthRole : IRole
{
    public void Compose(IRoleBuilder role)
    {
        role.Requires<PublicIngress>();
        role.Services(services => services.AddHealthChecks());
        role.Endpoints(endpoints => endpoints.MapHealthChecks("/health"));
    }
}

public sealed class PublicIngress : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.KestrelEndpoint("public");
        component.Use("routing", application => application.UseRouting());
    }
}
