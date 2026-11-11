using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using static Corfedo.Troupe.Testing.Tests.TestHosts;

namespace Corfedo.Troupe.Testing.Tests.HostBuilding;

// Changes that host-building listeners (WebApplicationFactory) queue for Build are owned by test:host.
public sealed class HostBuildingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void PlainBuildCreditsNothingToTestHost()
    {
        var generic = Generic(); generic.AddTroupe<ServiceDefinition>();
        using var host = generic.Build();
        Assert.DoesNotContain(host.Services.GetRequiredService<IComposition>().Manifest.Services, s => s.Owner.Kind == OwnerKind.Test);
        var web = Web(); web.AddTroupe<ServiceDefinition>();
        using var app = web.Build();
        Assert.DoesNotContain(app.Services.GetRequiredService<IComposition>().Manifest.Services, s => s.Owner.Kind == OwnerKind.Test);
    }

    [Fact]
    public void HostBuildingReplacementIsTestOwnedAndProgramRemovalFails()
    {
        var builder = Generic(); builder.AddTroupe<ServiceDefinition>();
        using (new HostBuildingHook(builder, h => h.ConfigureServices(s => { s.RemoveAll<Core>(); s.AddSingleton<Core, FakeCore>(); })))
        {
            using var host = builder.Build();
            Assert.IsType<FakeCore>(host.Services.GetRequiredService<Core>());
            var core = host.Services.GetRequiredService<IComposition>().Manifest.Services.Where(s => s.ServiceType == typeof(Core)).ToArray();
            Assert.Contains(core, s => !s.Active && s.RemovedBy!.Name == "test:host");
            Assert.Contains(core, s => s.Active && s.Owner.Name == "test:host");
        }
        var program = Generic(); program.AddTroupe<ServiceDefinition>();
        program.Services.RemoveAll<Core>();
        Assert.Contains(Assert.Throws<CompositionException>(() => program.Build()).Diagnostics, d => d.Code == "TRP021");
    }

    [Fact]
    public void HostBuildingChangesReachOnlyTheBuilderBeingBuilt()
    {
        var waiting = Generic(); waiting.AddTroupe<ServiceDefinition>();
        var built = Generic(); built.AddTroupe<ServiceDefinition>();
        using (new HostBuildingHook(built, h => h.ConfigureServices(s => s.AddSingleton<Marker>())))
        using (var host = built.Build())
            Assert.Contains(host.Services.GetRequiredService<IComposition>().Manifest.Services, s => s.ServiceType == typeof(Marker) && s.Owner.Name == "test:host");
        using var later = waiting.Build();
        Assert.DoesNotContain(later.Services.GetRequiredService<IComposition>().Manifest.Services, s => s.Owner.Kind == OwnerKind.Test);
    }

    [Fact]
    public void BuildAfterFailedCompositionRethrowsTheCompositionFailure()
    {
        var builder = Generic();
        Assert.Throws<CompositionException>(() => builder.AddTroupe<ThrowingDefinition>());
        var failure = Assert.Throws<CompositionException>(() => builder.Build());
        Assert.Contains(failure.Diagnostics, d => d.Code == "TRP017");
    }

    [Fact]
    public async Task ConcurrentBuildsAndHostingListenersComplete()
    {
        var builds = Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            var builder = i % 2 == 0 ? (IHostApplicationBuilder)Generic() : Web();
            builder.AddTroupe<ServiceDefinition>();
            using var hook = i % 4 == 1 ? new HostBuildingHook(builder, h => h.ConfigureServices(s => s.AddSingleton<Marker>())) : null;
            using var host = builder switch { WebApplicationBuilder w => (IHost)w.Build(), HostApplicationBuilder h => h.Build(), _ => throw new InvalidOperationException() };
            var test = host.Services.GetRequiredService<IComposition>().Manifest.Services.Any(s => s.Owner.Kind == OwnerKind.Test);
            Assert.Equal(i % 4 == 1, test);
        }, Token));
        var listeners = Enumerable.Range(0, 64).Select(_ => Task.Run(() => { using var l = new DiagnosticListener("Microsoft.Extensions.Hosting"); }, Token));
        var all = Task.WhenAll(builds.Concat(listeners));
        Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(60), Token)));
        await all;
    }

    [Fact]
    public void ComposeOnlyBuilderCanBeCollected()
    {
        var reference = ComposeOnly();
        Collect();
        Assert.False(reference.IsAlive, "A builder that was composed but never built is retained.");
    }

    [Fact]
    public void FailedCompositionSessionCanBeCollected()
    {
        var reference = FailedCompose();
        Collect();
        Assert.False(reference.IsAlive, "A session whose composition threw is retained.");
    }

    [Fact]
    public void AbandonedCompositionDoesNotRetainALaterBuiltBuilder()
    {
        _ = ComposeOnly();
        var reference = Built();
        Collect();
        Assert.False(reference.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ComposeOnly()
    {
        var builder = Generic();
        builder.AddTroupe<ServiceDefinition>();
        return new WeakReference(builder);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FailedCompose()
    {
        var builder = Generic();
        Assert.Throws<CompositionException>(() => builder.AddTroupe<ThrowingDefinition>());
        return new WeakReference(((IHostApplicationBuilder)builder).Properties.Values.Single(v => v.GetType().Name == "CompositionSession"));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Built()
    {
        var builder = Generic();
        builder.AddTroupe<ServiceDefinition>();
        using var host = builder.Build();
        return new WeakReference(builder);
    }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}

public class Core;
public sealed class FakeCore : Core;
public sealed class Marker;
public sealed class ServiceDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<ServiceComponent>(); }
public sealed class ServiceComponent : IComponent { public void Compose(IComponentBuilder c) => c.Services(s => s.AddSingleton<Core>()); }
public sealed class ThrowingDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<ThrowingComponent>(); }
public sealed class ThrowingComponent : IComponent { public void Compose(IComponentBuilder c) => c.Services(_ => throw new InvalidOperationException("callback failed")); }
