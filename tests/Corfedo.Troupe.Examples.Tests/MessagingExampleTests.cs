using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Corfedo.Troupe.AspNetCore;
using Corfedo.Troupe.Examples.Messaging;
using Corfedo.Troupe.Examples.Tests.Fixtures;
using Corfedo.Troupe.Testing;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Corfedo.Troupe.Examples.Tests;

public sealed class MessagingExampleTests
{
    [Fact]
    public async Task EveryDeclaredServingProfileVerifiesThroughTheRealApplicationAdapter()
    {
        var result = await CompositionVerifier.VerifyProfilesAsync<MessagingComposition>(
            profile => Builder(string.Join(',', profile.Roles)), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString()); Assert.Equal(3, result.Profiles.Count);
        foreach (var profile in result.Profiles)
        {
            Assert.All(profile.Manifest!.Contributions, contribution => Assert.True(contribution.Applied));
            Assert.Contains(profile.Manifest.Contributions, contribution => contribution.Owner.Type == typeof(MessageProcessing));
            Assert.Single(profile.Manifest.Services, service => service.ServiceType == typeof(IWolverineRuntime) && service.Active && service.Owner.Kind == OwnerKind.Application);
            Assert.All(profile.Manifest.Contributions, contribution => Assert.Equal(typeof(WolverineOptions), contribution.TargetType));
        }
    }
    [Theory]
    [InlineData("orders", true, false)]
    [InlineData("notifications", false, true)]
    [InlineData("orders,notifications", true, true)]
    [InlineData("notifications,orders", true, true)]
    public async Task StaticGeneratedHandlersServeOnlySelectedRolesWithScopedDisposal(string roles, bool orders, bool notifications)
    {
        var builder = Builder(roles); builder.FinalizeComposition();
        await using var application = builder.Build(); application.UseComposition();
        await application.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TypeLoadMode.Static, application.Services.GetRequiredService<WolverineOptions>().CodeGeneration.TypeLoadMode);
        Assert.Equal(orders, application.Services.GetService<OrderFormatter>() is not null);
        Assert.Equal(notifications, application.Services.GetService<NotificationFormatter>() is not null);
        using var client = Client(application);
        var journal = application.Services.GetRequiredService<MessageJournal>();
        using var orderResponse = await client.PostAsJsonAsync("/orders", new PlaceOrder(Guid.NewGuid().ToString("N")), TestContext.Current.CancellationToken);
        Assert.Equal(orders ? HttpStatusCode.OK : HttpStatusCode.NotFound, orderResponse.StatusCode);
        if (orders)
        {
            var first = await orderResponse.Content.ReadFromJsonAsync<DeliveryReceipt>(TestContext.Current.CancellationToken);
            Assert.Equal("orders", first!.Consumer); Assert.True(journal.IsScopeDisposed(first.ScopeId));
            using var secondResponse = await client.PostAsJsonAsync("/orders", new PlaceOrder(Guid.NewGuid().ToString("N")), TestContext.Current.CancellationToken);
            secondResponse.EnsureSuccessStatusCode();
            var second = await secondResponse.Content.ReadFromJsonAsync<DeliveryReceipt>(TestContext.Current.CancellationToken);
            Assert.NotEqual(first.ScopeId, second!.ScopeId); Assert.True(journal.IsScopeDisposed(second.ScopeId));
            Assert.True(journal.TryGet(first.Id, out var recorded)); Assert.Equal(first, recorded);
        }
        using var notificationResponse = await client.PostAsJsonAsync("/notifications", new SendNotification(Guid.NewGuid().ToString("N"), "reader@example.test"), TestContext.Current.CancellationToken);
        Assert.Equal(notifications ? HttpStatusCode.OK : HttpStatusCode.NotFound, notificationResponse.StatusCode);
        if (notifications)
        {
            var receipt = await notificationResponse.Content.ReadFromJsonAsync<DeliveryReceipt>(TestContext.Current.CancellationToken);
            Assert.Equal("notifications", receipt!.Consumer); Assert.True(journal.IsScopeDisposed(receipt.ScopeId));
        }
        await application.StopAsync(TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task QueuedHttpSubmissionRunsOrderAndCascadedReceiptHandlers()
    {
        using var errors = new ErrorObserver();
        var builder = Builder("orders"); builder.Logging.AddProvider(errors);
        await using var application = builder.Build(); application.UseComposition(); await application.StartAsync(TestContext.Current.CancellationToken);
        using var client = Client(application); var id = Guid.NewGuid().ToString("N");
        using var accepted = await client.PostAsJsonAsync("/orders/queued", new PlaceOrder(id), TestContext.Current.CancellationToken);
        Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, string.Join(Environment.NewLine, errors.Messages));
        var journal = application.Services.GetRequiredService<MessageJournal>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var receipt = await journal.WaitForAsync(id, timeout.Token);
        Assert.Equal("orders", receipt.Consumer);
        var retrieved = await client.GetFromJsonAsync<DeliveryReceipt>(accepted.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(receipt, retrieved);
        await application.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(journal.IsScopeDisposed(receipt.ScopeId));
        Assert.Empty(errors.Messages);
    }
    [Fact]
    public async Task ContributionOwnershipAndOrderAreIndependentOfSelectionOrder()
    {
        var snapshots = new List<CompositionManifest>();
        foreach (var roles in new[] { "orders,notifications", "notifications,orders" })
        {
            var builder = Builder(roles); await using var application = builder.Build(); application.UseComposition();
            var manifest = application.Services.GetRequiredService<IComposition>().Manifest; snapshots.Add(manifest);
            Assert.Single(manifest.Contributors, contributor => contributor.Owner.Type == typeof(MessageProcessing));
            Assert.Single(manifest.Services, service => service.ServiceType == typeof(MessageJournal) && service.Active);
        }
        Assert.Equal(snapshots[0].Contributions.Select(c => c.Owner), snapshots[1].Contributions.Select(c => c.Owner));
        Assert.Equal(3, snapshots[0].Contributions.Count);
    }
    [Fact]
    public async Task IncompleteCompiledArtifactFailsStaticStartupForTheMissingConsumer()
    {
        var builder = Builder("orders,notifications");
        await using var application = builder.Build(); application.UseComposition();
        var options = application.Services.GetRequiredService<WolverineOptions>();
        options.ApplicationAssembly = typeof(ArtifactAssembly).Assembly;
        var failure = await Assert.ThrowsAsync<MissingPreBuiltTypesException>(() => application.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains(nameof(SendNotificationHandler), failure.Message);
        Assert.DoesNotContain(nameof(PlaceOrderHandler), failure.Message);
        Assert.DoesNotContain(nameof(DeliveryReceiptHandler), failure.Message);
        Assert.Equal(TypeLoadMode.Static, options.CodeGeneration.TypeLoadMode);
    }
    [Fact]
    public async Task GenerationHostContainsConsumerUnionWithoutStarting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "troupe-generation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        IHost? host = null;
        try
        {
            var builder = MessagingApplication.CreateGenerationBuilder(directory); host = builder.Build();
            var composition = host.Services.GetRequiredService<IComposition>();
            Assert.Equal("generate", Assert.Single(composition.Roles).Name);
            Assert.DoesNotContain(composition.Manifest.Contributors, c => c.Owner.Type == typeof(ReceiptEndpoints));
            Assert.Null(host.Services.GetService<IServer>());
            Assert.Equal(3, composition.Manifest.Contributions.Count);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync(); else host?.Dispose();
            Directory.Delete(directory, true);
        }
    }
    [Fact]
    public async Task GenerationCliWritesAllAdaptersInAnIsolatedProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "troupe-codegen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(MessagingApplication).Assembly.Location);
            start.ArgumentList.Add("codegen"); start.ArgumentList.Add("write");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken); var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await output + Environment.NewLine + await error);
                var files = Directory.GetFiles(Path.Combine(directory, "Internal", "Generated"), "*.cs", SearchOption.AllDirectories);
                Assert.Contains(files, file => Path.GetFileName(file).StartsWith(nameof(PlaceOrderHandler), StringComparison.Ordinal));
                Assert.Contains(files, file => Path.GetFileName(file).StartsWith(nameof(SendNotificationHandler), StringComparison.Ordinal));
                Assert.Contains(files, file => Path.GetFileName(file).StartsWith(nameof(DeliveryReceiptHandler), StringComparison.Ordinal));
                Assert.Contains(files, file => Path.GetFileName(file) == "GeneratedHandlerRegistry.cs");
                var expectedRoot = Path.Combine(ApplicationExamplesTests.ContentRoot("Messaging"), "Internal", "Generated");
                var actualRoot = Path.Combine(directory, "Internal", "Generated");
                var expectedFiles = Directory.GetFiles(expectedRoot, "*.cs", SearchOption.AllDirectories);
                Assert.Equal(expectedFiles.Select(file => Path.GetRelativePath(expectedRoot, file)).Order(StringComparer.Ordinal),
                    files.Select(file => Path.GetRelativePath(actualRoot, file)).Order(StringComparer.Ordinal));
                foreach (var expected in expectedFiles)
                    Assert.Equal(await File.ReadAllTextAsync(expected, TestContext.Current.CancellationToken),
                        await File.ReadAllTextAsync(Path.Combine(actualRoot, Path.GetRelativePath(expectedRoot, expected)), TestContext.Current.CancellationToken));
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(output, error);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class ErrorObserver : ILoggerProvider, ILogger
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) Messages.Enqueue(formatter(state, exception) + Environment.NewLine + exception); }
        public void Dispose() { }
    }
    private static WebApplicationBuilder Builder(string roles)
    {
        var builder = MessagingApplication.CreateBuilder(["--roles", roles, "--Kestrel:Endpoints:public:Url", "http://127.0.0.1:0"], ApplicationExamplesTests.ContentRoot("Messaging"));
        builder.Logging.ClearProviders(); return builder;
    }
    private static HttpClient Client(WebApplication application)
    {
        var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        return new() { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };
    }
}
