using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using static Corfedo.Troupe.Tests.CoreTests;

namespace Corfedo.Troupe.Tests;

public sealed class OptionsAndHostingTests
{
    public OptionsAndHostingTests() => _ = new CoreTests();
    private static HostApplicationBuilder OptionsBuilder(string value = "2")
    {
        var builder = Builder("a"); builder.Configuration["Probe:Count"] = value;
        A.Declare = r => r.Options<Probe, Positive>("Probe"); return builder;
    }
    [Fact]
    public void MissingRequiredConfigurationIsAnErrorAndManifestContainsOnlyDeclaredReads()
    {
        A.Declare = r => { r.Options<Probe>("Secret"); r.ReadsConfiguration("Manual"); };
        var error = Assert.Throws<CompositionException>(() => Builder("a").Compose<Definition>());
        Assert.Equal(2, error.Diagnostics.Count(d => d.Code == "TRP005"));
        Assert.Equal(["Secret", "Manual"], error.Manifest!.Configuration.Select(c => c.Section));
    }
    [Fact]
    public async Task RequiredOptionsAlsoFailValueAndStartupIfSectionDisappears()
    {
        var builder = OptionsBuilder(); builder.Compose<Definition>();
        builder.Configuration.Sources.Clear();
        using var host = builder.Build();
        var valueError = Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<Probe>>().Value);
        Assert.Contains("a: required section Probe is missing", valueError.Message);
        var startupError = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("required section Probe is missing", startupError.Message);
    }
    [Theory]
    [InlineData("-1", typeof(OptionsValidationException))]
    [InlineData("not-an-integer", typeof(InvalidOperationException))]
    public async Task InvalidOptionsFailBothValueAndStartup(string value, Type errorType)
    {
        var builder = OptionsBuilder(value); builder.Compose<Definition>();
        using var host = builder.Build();
        Assert.IsType(errorType, Record.Exception(() => host.Services.GetRequiredService<IOptions<Probe>>().Value));
        Assert.IsType(errorType, await Record.ExceptionAsync(() => host.StartAsync(TestContext.Current.CancellationToken)));
    }
    [Theory]
    [InlineData("Probe")]
    [InlineData("Other")]
    public void SecondDeclarationForAnOptionsTypeIsAnError(string section)
    {
        var builder = OptionsBuilder(); builder.Configuration["Roles"] = "a,b";
        B.Declare = r => r.Options<Probe>(section);
        Assert.Contains("TRP007", Assert.Throws<CompositionException>(() => builder.Compose<Definition>()).Message);
    }
    [Fact]
    public async Task ValidatorsAreExplicitConstructedWithDiAndComposeNormally()
    {
        var builder = OptionsBuilder();
        A.Declare = r =>
        {
            r.Options<Probe, Positive>("Probe");
            r.Services(s => { s.AddSingleton<ValidationDependency>(); s.AddSingleton<IValidateOptions<Probe>, Even>(); });
        };
        var manifest = builder.Compose<Definition>();
        using var host = builder.Build();
        Assert.Equal(2, host.Services.GetRequiredService<IOptions<Probe>>().Value.Count);
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Single(manifest.Configuration, r => r.OptionsType == typeof(Probe));
        Assert.DoesNotContain(manifest.Diagnostics, d => d.Code == "TRP001");
        var bad = OptionsBuilder("3");
        A.Declare = r => { r.Options<Probe, Positive>("Probe"); r.Services(s => { s.AddSingleton<ValidationDependency>(); s.AddSingleton<IValidateOptions<Probe>, Even>(); }); };
        bad.Compose<Definition>(); using var badHost = bad.Build();
        Assert.Contains("even", Assert.Throws<OptionsValidationException>(() => badHost.Services.GetRequiredService<IOptions<Probe>>().Value).Message);
    }
    [Fact]
    public async Task ConstructorAndStartupValidationPrecedeSequentialChecksAndServiceStart()
    {
        var events = new List<string>();
        var builder = OptionsBuilder();
        A.Declare = r =>
        {
            r.Options<Probe>("Probe");
            r.Services(s =>
            {
                s.AddSingleton<IValidateOptions<Probe>>(new RecordingValidator(events));
                s.AddSingleton<IHostedService>(_ => new RecordingWorker(events));
            });
            r.Check(async (_, _) => { events.Add("check1"); await Task.Yield(); events.Add("check1-end"); });
            r.Check((_, _) => { events.Add("check2"); return Task.CompletedTask; });
        };
        builder.Compose<Definition>(); using var host = builder.Build();
        Assert.Empty(events);
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["constructor", "validation", "check1", "check1-end", "check2", "start"], events);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task OtherLifecycleCallbacksCanOverlapButTroupeChecksRemainSequential()
    {
        var otherEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompleted = false;
        var builder = Builder("a");
        builder.Services.Configure<HostOptions>(o => o.ServicesStartConcurrently = true);
        builder.Services.AddSingleton<IHostedService>(new OtherLifecycle(firstEntered, otherEntered));
        A.Declare = r =>
        {
            r.Check(async (_, token) => { firstEntered.SetResult(); await otherEntered.Task.WaitAsync(token); firstCompleted = true; });
            r.Check((_, _) => { Assert.True(firstCompleted); return Task.CompletedTask; });
        };
        builder.Compose<Definition>(); using var host = builder.Build();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await host.StartAsync(timeout.Token);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
    [Fact]
    public async Task ChecksPreserveOwnerFailuresAndCancellation()
    {
        var builder = Builder("a");
        A.Declare = r => r.Check((_, _) => throw new InvalidOperationException("specific failure"));
        builder.Compose<Definition>(); using var host = builder.Build();
        var error = await Assert.ThrowsAsync<CompositionCheckException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal("a", error.Owner.Name); Assert.Equal("specific failure", error.InnerException!.Message);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledBuilder = Builder("a");
        A.Declare = r => r.Check(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        canceledBuilder.Compose<Definition>(); using var canceledHost = canceledBuilder.Build();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var starting = canceledHost.StartAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiFactoryResourcesAreDisposedWithoutStartAndAfterFailedStart(bool failStart)
    {
        var state = new ResourceState(); var builder = Builder("a");
        A.Declare = r =>
        {
            r.Services(s => s.AddSingleton(_ => new Resource(state)));
            if (failStart) r.Check((sp, _) => { sp.GetRequiredService<Resource>(); throw new InvalidOperationException("failed check"); });
        };
        builder.Compose<Definition>();
        var host = builder.Build();
        Assert.Equal(0, state.Created);
        try
        {
            if (failStart) await Assert.ThrowsAsync<CompositionCheckException>(() => host.StartAsync(TestContext.Current.CancellationToken));
            else host.Services.GetRequiredService<Resource>();
            Assert.Equal(1, state.Created); Assert.Equal(0, state.Disposed);
        }
        finally { if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync(); else host.Dispose(); }
        Assert.Equal(1, state.Disposed);
    }
    [Fact]
    public void BuildValidationDoesNotExecuteFactoriesAndDoesValidateKnownCallSites()
    {
        var calls = 0; var builder = Builder("a");
        A.Declare = r => r.Services(s => s.AddSingleton<Foo>(_ => { calls++; throw new InvalidOperationException("factory"); }));
        builder.Compose<Definition>(); using var host = builder.Build();
        Assert.Equal(0, calls);
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<Foo>());
        Assert.Equal(1, calls);
        var invalid = Builder("a"); A.Declare = r => r.Services(s => s.AddSingleton<MissingDependency>());
        invalid.Compose<Definition>(); Assert.Throws<AggregateException>(() => invalid.Build());
    }
    [Fact]
    public void GraphAndCompositionFailureDoNotAcquireResourcesAndCapturedInstancesRemainApplicationOwned()
    {
        var state = new ResourceState();
        A.Declare = r => { r.Services(s => s.AddSingleton(_ => new Resource(state))); r.Requires<Cycle1>(); };
        Assert.Throws<CompositionException>(() => Builder("a").Compose<Definition>());
        Assert.Equal(0, state.Created);
        var resource = new Resource(state);
        try
        {
            var builder = Builder("a"); A.Declare = r => r.Services(s => s.AddSingleton(resource));
            builder.Compose<Definition>(); using (var host = builder.Build()) host.Services.GetRequiredService<Resource>();
            Assert.Equal(0, state.Disposed);
        }
        finally { resource.Dispose(); }
        Assert.Equal(1, state.Disposed);
    }
    public sealed class Probe { public int Count { get; set; } }
    public sealed class Positive : IValidateOptions<Probe>
    { public ValidateOptionsResult Validate(string? name, Probe p) => p.Count > 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail("positive"); }
    public sealed class ValidationDependency;
    public sealed class Even(ValidationDependency dependency) : IValidateOptions<Probe>
    {
        public ValidateOptionsResult Validate(string? name, Probe p) => dependency is not null && p.Count % 2 == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail("even");
    }
    private sealed class RecordingValidator(List<string> events) : IValidateOptions<Probe>
    { public ValidateOptionsResult Validate(string? name, Probe p) { events.Add("validation"); return ValidateOptionsResult.Success; } }
    private sealed class RecordingWorker : IHostedService
    {
        private readonly List<string> events;
        public RecordingWorker(List<string> events) { this.events = events; events.Add("constructor"); }
        public Task StartAsync(CancellationToken c) { events.Add("start"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken c) => Task.CompletedTask;
    }
    private sealed class OtherLifecycle(TaskCompletionSource first, TaskCompletionSource other) : IHostedLifecycleService
    {
        public async Task StartingAsync(CancellationToken c) { await first.Task.WaitAsync(c); other.SetResult(); }
        public Task StartAsync(CancellationToken c) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken c) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken c) => Task.CompletedTask;
        public Task StopAsync(CancellationToken c) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken c) => Task.CompletedTask;
    }
    public sealed class ResourceState { public int Created; public int Disposed; }
    public sealed class Resource : IDisposable
    {
        private readonly ResourceState state;
        public Resource(ResourceState state) { this.state = state; state.Created++; }
        public void Dispose() => state.Disposed++;
    }
    public sealed class MissingDependency(Unregistered dependency) { public Unregistered Dependency { get; } = dependency; }
    public sealed class Unregistered;
}
