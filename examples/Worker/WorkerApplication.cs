using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe.Examples.Worker;

public static class WorkerApplication
{
    public static HostApplicationBuilder CreateBuilder(string[] args, string? contentRootPath = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        { Args = args, ContentRootPath = contentRootPath ?? AppContext.BaseDirectory });
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        builder.Compose<WorkerComposition>();
        return builder;
    }
}
