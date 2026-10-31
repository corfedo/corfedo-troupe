using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine;

namespace Corfedo.Troupe.Examples.Messaging;

public static class MessagingApplication
{
    public static WebApplicationBuilder CreateBuilder(string[] args, string? contentRootPath = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args, ContentRootPath = contentRootPath ?? AppContext.BaseDirectory,
            ApplicationName = typeof(MessagingApplication).Assembly.GetName().Name
        });
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
        builder.WebHost.ConfigureKestrel(options => options.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: false));
        builder.Compose<MessagingComposition>();
        ConfigureWolverine(builder, TypeLoadMode.Static);
        return builder;
    }

    public static HostApplicationBuilder CreateGenerationBuilder(string contentRootPath)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true, ContentRootPath = contentRootPath,
            ApplicationName = typeof(MessagingApplication).Assembly.GetName().Name
        });
        builder.Configuration["Roles"] = "generate";
        builder.Services.Configure<JasperFxOptions>(options =>
            options.GeneratedCodeOutputPath = Path.Combine(contentRootPath, "Internal", "Generated"));
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        builder.Compose<HandlerGenerationComposition>();
        ConfigureWolverine(builder, TypeLoadMode.Dynamic);
        builder.FinalizeComposition();
        return builder;
    }

    private static void ConfigureWolverine(IHostApplicationBuilder builder, TypeLoadMode mode)
    {
        // IHostApplicationBuilder's overload applies its callback during registration, before finalization.
        builder.UseWolverine(options =>
        {
            options.ApplicationAssembly = typeof(MessagingApplication).Assembly;
            options.Discovery.DisableConventionalDiscovery();
            options.CodeGeneration.TypeLoadMode = mode;
            builder.ApplyContributions(options);
        });
    }
}
