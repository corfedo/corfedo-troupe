using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe.Examples.Worker;

public static class WorkerApplication
{
    public static HostApplicationBuilder CreateBuilder(string[] args, string? contentRootPath = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        { Args = args, ContentRootPath = contentRootPath ?? AppContext.BaseDirectory });
        builder.AddTroupe<WorkerComposition>();
        return builder;
    }
}
