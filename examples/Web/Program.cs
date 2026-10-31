using Corfedo.Troupe;
using Corfedo.Troupe.AspNetCore;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
builder.WebHost.ConfigureKestrel(options => options.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: false));
builder.Compose<WebComposition>();
builder.FinalizeComposition();
await using var app = builder.Build();
app.UseComposition();
await app.RunAsync();

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
