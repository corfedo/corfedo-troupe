using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Corfedo.Troupe.AspNetCore;
using Corfedo.Troupe.Examples.Web;
using Corfedo.Troupe.Examples.Worker;
using Corfedo.Troupe.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Corfedo.Troupe.Examples.Tests;

public sealed class ApplicationExamplesTests
{
    internal static string ContentRoot(string example) => Path.GetFullPath(Path.Combine(
        typeof(ApplicationExamplesTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ExamplesDirectory").Value!, example));

    [Fact]
    public async Task WorkerServingProfileVerifiesTheRealDefinition()
    {
        var result = await CompositionVerifier.VerifyProfilesAsync<WorkerComposition>(
            profile => WorkerApplication.CreateBuilder(["--profile", profile.Name], ContentRoot("Worker")),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString());
        var profile = Assert.Single(result.Profiles);
        Assert.Equal("worker", profile.Profile.Name);
        Assert.Contains(profile.Manifest!.Configuration, read => read.OptionsType == typeof(HeartbeatOptions) && read.Required);
        Assert.Null(profile.AspNetManifest);
    }
    [Fact]
    public async Task WorkerStartsAndEmitsAHeartbeatThroughTheRealHostedService()
    {
        using var observer = new HeartbeatObserver();
        var builder = WorkerApplication.CreateBuilder(["--Worker:IntervalSeconds", "1"], ContentRoot("Worker"));
        builder.Logging.ClearProviders(); builder.Logging.AddProvider(observer);
        builder.FinalizeComposition();
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await observer.Heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var composition = host.Services.GetRequiredService<IComposition>();
        Assert.True(composition.Contains<WorkerRole>());
        Assert.Single(composition.Manifest.Services, s => s.ImplementationType == typeof(Heartbeat) && s.Active);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task WorkerOptionsRejectInvalidValuesBeforeTheWorkloadStarts()
    {
        using var observer = new HeartbeatObserver();
        var builder = WorkerApplication.CreateBuilder(["--Worker:IntervalSeconds", "0"], ContentRoot("Worker"));
        builder.Logging.ClearProviders(); builder.Logging.AddProvider(observer);
        using var host = builder.Build();
        Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<HeartbeatOptions>>().Value);
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.False(observer.Heartbeat.Task.IsCompleted);
    }
    [Fact]
    public async Task WebServingProfilesVerifyWithTheApplicationBuilderAndPipeline()
    {
        var result = await CompositionVerifier.VerifyProfilesAsync<WebComposition>(
            profile => WebBuilder(profile.Name), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString());
        Assert.Equal(2, result.Profiles.Count);
        Assert.All(result.Profiles, p => Assert.Equal(["routing"], p.AspNetManifest!.Stages));
        var single = result.Profiles.Single(p => p.Profile.Name == "web");
        var combined = result.Profiles.Single(p => p.Profile.Name == "combined");
        Assert.Single(single.AspNetManifest!.Endpoints);
        Assert.Equal(2, combined.AspNetManifest!.Endpoints.Count);
    }
    [Theory]
    [InlineData("web", false)]
    [InlineData("combined", true)]
    public async Task WebProfilesExposeOnlyTheirSelectedEndpointsOverKestrel(string profile, bool health)
    {
        var builder = WebBuilder(profile); builder.FinalizeComposition();
        await using var application = builder.Build(); application.UseComposition();
        await application.StartAsync(TestContext.Current.CancellationToken);
        var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
        var response = await client.GetFromJsonAsync<RolesResponse>("/", TestContext.Current.CancellationToken);
        Assert.Equal(health ? ["health", "web"] : new[] { "web" }, response!.Roles);
        using var status = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(health ? HttpStatusCode.OK : HttpStatusCode.NotFound, status.StatusCode);
        if (health) Assert.Equal("Healthy", await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var composition = application.Services.GetRequiredService<IComposition>();
        Assert.All(composition.AspNetCore().Endpoints, e => Assert.Equal(OwnerKind.Role, e.Owner.Kind));
        await application.StopAsync(TestContext.Current.CancellationToken);
    }
    [Fact]
    public void ExampleRejectsUnsupportedServingCombinationsAndExtraListeners()
    {
        Assert.Throws<CompositionException>(() => WebBuilder("health"));
        var builder = WebBuilder("web");
        builder.Configuration["Kestrel:Endpoints:extra:Url"] = "http://127.0.0.1:0";
        Assert.Contains("TRP103", Assert.Throws<CompositionException>(() => builder.FinalizeComposition()).Message);
    }
    private static WebApplicationBuilder WebBuilder(string profile)
    {
        var builder = WebExample.CreateBuilder(["--profile", profile, "--Kestrel:Endpoints:public:Url", "http://127.0.0.1:0"], ContentRoot("Web"));
        builder.Logging.ClearProviders(); return builder;
    }
    private sealed record RolesResponse(string[] Roles);
    private sealed class HeartbeatObserver : ILoggerProvider, ILogger
    {
        internal TaskCompletionSource Heartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (formatter(state, exception) == "Heartbeat.") Heartbeat.TrySetResult(); }
        public void Dispose() { }
    }
}
