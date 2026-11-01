namespace Corfedo.Troupe.Examples.Operations;

public static class OperationsApplication
{
    public static WebApplicationBuilder CreateBuilder(string[] args, string? contentRootPath = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args, ContentRootPath = contentRootPath ?? AppContext.BaseDirectory,
            ApplicationName = typeof(OperationsApplication).Assembly.GetName().Name
        });
        builder.AddTroupe<OperationsComposition>();
        return builder;
    }
}
