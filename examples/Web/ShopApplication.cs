namespace Corfedo.Troupe.Examples.Web;

public static class ShopApplication
{
    public static WebApplicationBuilder CreateBuilder(string[] args, string? contentRootPath = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = args, ContentRootPath = contentRootPath ?? AppContext.BaseDirectory });
        builder.AddTroupe<ShopComposition>();
        return builder;
    }
}
