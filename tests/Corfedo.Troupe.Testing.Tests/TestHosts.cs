using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Corfedo.Troupe.Testing.Tests;

public static class TestHosts
{
    public static HostApplicationBuilder Generic(Dictionary<string, string?>? settings = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "Production" });
        if (settings is not null) builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
    public static WebApplicationBuilder Web()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory, ApplicationName = typeof(TestHosts).Assembly.GetName().Name
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        return builder;
    }
}

// Queues IHostBuilder callbacks at HostBuilding for one builder, as WebApplicationFactory does.
public sealed class HostBuildingHook : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly IDictionary<object, object> properties;
    private readonly Action<IHostBuilder> configure;
    private readonly IDisposable subscription;
    public HostBuildingHook(IHostApplicationBuilder builder, Action<IHostBuilder> configure)
    { properties = builder.Properties; this.configure = configure; subscription = DiagnosticListener.AllListeners.Subscribe(this); }
    public void OnNext(DiagnosticListener listener) { if (listener.Name == "Microsoft.Extensions.Hosting") listener.Subscribe(this); }
    public void OnNext(KeyValuePair<string, object?> value)
    { if (value.Key == "HostBuilding" && value.Value is IHostBuilder host && ReferenceEquals(host.Properties, properties)) configure(host); }
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose() => subscription.Dispose();
}
