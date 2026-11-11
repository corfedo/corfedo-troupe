using System.Collections.Concurrent;
using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Corfedo.Troupe.Aspire.Hosting.Tests;

// A started test AppHost. Disposing stops it, and then kills any resource process it observed that is still running.
internal sealed class AppHostRun : IAsyncDisposable
{
    private readonly CancellationTokenSource watching = new();
    private readonly ConcurrentDictionary<int, bool> processes = new();
    private readonly Task watcher;

    private AppHostRun(DistributedApplication app)
    {
        App = app;
        watcher = WatchAsync();
    }

    internal DistributedApplication App { get; }
    internal static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static IDistributedApplicationTestingBuilder Builder(params string[] args) =>
        DistributedApplicationTestingBuilder.Create(["--ASPIRE_ALLOW_UNSECURED_TRANSPORT=true", .. args]);

    internal static async Task<AppHostRun> StartAsync(IDistributedApplicationTestingBuilder builder)
    {
        var app = await builder.BuildAsync(Token);
        var run = new AppHostRun(app);
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(Token);
            startup.CancelAfter(TimeSpan.FromMinutes(2));
            await app.StartAsync(startup.Token);
            return run;
        }
        catch
        {
            await run.DisposeAsync();
            throw;
        }
    }

    internal async Task<CustomResourceSnapshot> RunningAsync(string resource)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        return (await App.ResourceNotifications.WaitForResourceAsync(resource, e => e.Snapshot.State?.Text == KnownResourceStates.Running, timeout.Token)).Snapshot;
    }

    internal async Task<string> GetAsync(string resource, string endpoint, string path)
    {
        using var client = App.CreateHttpClient(resource, endpoint);
        client.Timeout = TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return await client.GetStringAsync(path, Token); }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !Token.IsCancellationRequested)
            {
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"GET {path} on {resource} failed.{Environment.NewLine}{await LogsAsync(resource)}", exception);
                await Task.Delay(250, Token);
            }
        }
    }

    // The resource's console output so far, for failure messages.
    internal async Task<string> LogsAsync(string resource)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var lines = new List<string>();
        if (!App.ResourceNotifications.TryGetCurrentState(resource, out var state)) return "";
        try
        {
            await foreach (var batch in App.Services.GetRequiredService<ResourceLoggerService>().WatchAsync(state.ResourceId).WithCancellation(timeout.Token))
                lines.AddRange(batch.Select(l => l.Content));
        }
        catch (OperationCanceledException) { }
        return string.Join(Environment.NewLine, lines);
    }

    private async Task WatchAsync()
    {
        try
        {
            await foreach (var change in App.ResourceNotifications.WatchAsync(watching.Token))
                foreach (var property in change.Snapshot.Properties)
                    if (property.Name == "executable.pid" && int.TryParse(property.Value?.ToString(), out var pid) && pid > 0)
                        processes.TryAdd(pid, true);
        }
        catch (OperationCanceledException) when (watching.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        // An AppHost that failed to start has nothing to stop, and StopAsync may throw for it.
        try { await App.StopAsync(CancellationToken.None); }
        catch (Exception) { }
        await App.DisposeAsync();
        await watching.CancelAsync();
        await watcher;
        watching.Dispose();
        foreach (var pid in processes.Keys)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }
}
