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
    public async Task WorkerVerifiesTheRealDefinitionWithoutRoles()
    {
        var result = await CompositionVerifier.VerifyAsync<WorkerComposition>(
            () => WorkerApplication.CreateBuilder([], ContentRoot("Worker")), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString());
        var entry = Assert.Single(result.Profiles);
        Assert.Null(entry.Profile);
        Assert.Empty(entry.Manifest!.Roles);
        Assert.Contains(entry.Manifest.Configuration, read => read.OptionsType == typeof(HeartbeatOptions) && read.Required);
        Assert.Equal([OwnerKind.Definition, OwnerKind.Component, OwnerKind.Component],
            entry.Manifest.PathTo<WorkerSettingsComponent>().Select(o => o.Kind));
        Assert.Null(entry.AspNetManifest);
    }
    [Fact]
    public async Task WorkerStartsAndEmitsAHeartbeatThroughTheRealHostedService()
    {
        using var observer = new HeartbeatObserver();
        var builder = WorkerApplication.CreateBuilder(["--Worker:IntervalSeconds", "1"], ContentRoot("Worker"));
        builder.Logging.ClearProviders(); builder.Logging.AddProvider(observer);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await observer.Heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var composition = host.Services.GetRequiredService<IComposition>();
        Assert.True(composition.Includes<HeartbeatComponent>());
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
    public async Task WebVerifiesWithTheApplicationBuilderAndPipeline()
    {
        var result = await CompositionVerifier.VerifyAsync<ShopComposition>(WebBuilder, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString());
        var entry = Assert.Single(result.Profiles);
        Assert.Equal(["routing"], entry.AspNetManifest!.Stages);
        Assert.Equal(2, entry.AspNetManifest.Endpoints.Count);
        Assert.Single(entry.Manifest!.Contributors, c => c.Owner.Type == typeof(ProductsComponent));
        Assert.Single(entry.Manifest.Configuration, read => read.OptionsType == typeof(ProductOptions) && read.Required);
    }
    [Fact]
    public async Task WebServesTheComponentsEndpointsOverKestrel()
    {
        var builder = WebBuilder();
        await using var application = builder.Build(); application.UseComposition();
        await application.StartAsync(TestContext.Current.CancellationToken);
        var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
        var products = await client.GetFromJsonAsync<Product[]>("/products", TestContext.Current.CancellationToken);
        Assert.Equal(["tea", "mug"], products!.Select(p => p.Sku));
        using var order = await client.PostAsJsonAsync("/orders", new OrderRequest("tea", 2), TestContext.Current.CancellationToken);
        Assert.Equal(9m, (await order.Content.ReadFromJsonAsync<OrderConfirmation>(TestContext.Current.CancellationToken))!.Total);
        using var unknown = await client.PostAsJsonAsync("/orders", new OrderRequest("cake", 1), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var composition = application.Services.GetRequiredService<IComposition>();
        Assert.Null(composition.Profile);
        Assert.Equal([nameof(CatalogComponent), nameof(OrdersComponent)], composition.AspNetCore().Endpoints.Select(e => e.Owner.Type!.Name).Order(StringComparer.Ordinal));
        await application.StopAsync(TestContext.Current.CancellationToken);
    }
    [Fact]
    public void ExampleRejectsSelectionsMissingSectionsAndExtraListeners()
    {
        Assert.Contains("TRP013", Assert.Throws<CompositionException>(() => ShopApplication.CreateBuilder(["--profile", "orders"], ContentRoot("Web"))).Message);
        var empty = Directory.CreateTempSubdirectory("troupe-web-");
        try
        {
            var missing = Assert.Throws<CompositionException>(() => ShopApplication.CreateBuilder([], empty.FullName));
            Assert.Contains(missing.Diagnostics, d => d.Code == "TRP005" && d.Owners.Single().Type == typeof(ProductsComponent));
        }
        finally { empty.Delete(); }
        var builder = WebBuilder();
        builder.Configuration["Kestrel:Endpoints:extra:Url"] = "http://127.0.0.1:0";
        Assert.Contains("TRP103", Assert.Throws<CompositionException>(() => builder.Build()).Message);
    }
    private static WebApplicationBuilder WebBuilder()
    {
        var builder = ShopApplication.CreateBuilder(["--Kestrel:Endpoints:public:Url", "http://127.0.0.1:0"], ContentRoot("Web"));
        builder.Logging.ClearProviders(); return builder;
    }
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
