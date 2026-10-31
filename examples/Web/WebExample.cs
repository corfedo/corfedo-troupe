namespace Corfedo.Troupe.Examples.Web;

public static class WebExample
{
    public static WebApplicationBuilder CreateBuilder(string[] args, string? contentRootPath = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = args, ContentRootPath = contentRootPath ?? AppContext.BaseDirectory });
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
        builder.Compose<WebComposition>();
        return builder;
    }
}
