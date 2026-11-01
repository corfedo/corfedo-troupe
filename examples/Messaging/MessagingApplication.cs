using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;

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
        builder.AddTroupe<MessagingComposition>();
        ConfigureWolverine(builder, TypeLoadMode.Static);
        return builder;
    }

    public static WebApplicationBuilder CreateDurableBuilder(string[] args, string? contentRootPath = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args, ContentRootPath = contentRootPath ?? AppContext.BaseDirectory,
            ApplicationName = typeof(MessagingApplication).Assembly.GetName().Name
        });
        builder.AddTroupe<DurableMessagingComposition>();
        ConfigureWolverine(builder, TypeLoadMode.Static, durable: true);
        return builder;
    }

    public static HostApplicationBuilder CreateDurableProvisioningBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration["Profile"] = "provision";
        builder.AddTroupe<DurableProvisioningComposition>();
        ConfigureWolverine(builder, TypeLoadMode.Static, durable: true);
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
        builder.AddTroupe<HandlerGenerationComposition>();
        ConfigureWolverine(builder, TypeLoadMode.Dynamic);
        return builder;
    }

    private static void ConfigureWolverine(IHostApplicationBuilder builder, TypeLoadMode mode, bool durable = false)
    {
        // IHostApplicationBuilder's overload applies its callback during registration, before finalization.
        builder.UseWolverine(options =>
        {
            options.ApplicationAssembly = typeof(MessagingApplication).Assembly;
            options.Discovery.DisableConventionalDiscovery();
            options.CodeGeneration.TypeLoadMode = mode;
            builder.ApplyContributions(options);
            if (durable)
            {
                var connection = builder.Configuration["Messaging:ConnectionString"];
                if (string.IsNullOrWhiteSpace(connection))
                    throw new InvalidOperationException("Messaging:ConnectionString is required.");
                options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                options.PersistMessagesWithPostgresql(connection, "troupe_messages").OverrideAutoCreateResources(AutoCreate.None);
                var transport = options.Transports.GetOrCreate<PostgresqlTransport>();
                transport.AutoProvision = false;
                transport.MessageStorageSchemaName = "troupe_messages";
                transport.TransportSchemaName = "troupe_queues";
            }
        });
    }
}
