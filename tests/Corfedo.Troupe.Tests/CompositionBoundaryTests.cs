using System.Runtime.CompilerServices;
using Corfedo.Troupe.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using static Corfedo.Troupe.Tests.CoreTests;

namespace Corfedo.Troupe.Tests;

public sealed class CompositionBoundaryTests
{
    public CompositionBoundaryTests() => _ = new CoreTests();

    [Theory]
    [InlineData("Requires")]
    [InlineData("Services")]
    [InlineData("Options")]
    [InlineData("ReadsConfiguration")]
    [InlineData("Contribute")]
    [InlineData("Check")]
    [InlineData("MultipleAllowed")]
    [InlineData("ExclusiveService")]
    public void LateDeclarationsInsideServicesHaveAnOwnerAndFailureSnapshot(string operation)
    {
        A.Declare = role => role.Services(_ =>
        {
            switch (operation)
            {
                case "Requires": role.Requires<Store>(); break;
                case "Services": role.Services(_ => { }); break;
                case "Options": role.Options<Probe>("Probe"); break;
                case "ReadsConfiguration": role.ReadsConfiguration("Probe", false); break;
                case "Contribute": role.Contribute<List<string>>(_ => { }); break;
                case "Check": role.Check((_, _) => Task.CompletedTask); break;
                case "MultipleAllowed": role.MultipleAllowed<Foo>(); break;
                case "ExclusiveService": role.ExclusiveService<Foo>(); break;
            }
        });
        var exception = Assert.Throws<CompositionException>(() => Builder("a").AddTroupe<Definition>());
        Assert.Contains(exception.Diagnostics, d => d.Code == "TRP015" && d.Owners.Single().Name == "a");
        Assert.Contains(exception.Manifest!.Diagnostics, d => d.Code == "TRP015");
        Assert.IsType<CompositionException>(exception.InnerException);
    }
    [Fact]
    public void RetainedContributorAndDefinitionBuildersRejectFurtherDeclarations()
    {
        IRoleBuilder? retained = null; A.Declare = r => retained = r;
        Builder("a").AddTroupe<Definition>();
        Assert.Contains("TRP015", Assert.Throws<CompositionException>(() => retained!.Requires<Store>()).Message);
        var builder = Builder("a"); builder.AddTroupe<RetainedDefinition>();
        Assert.Throws<CompositionException>(() => RetainedDefinition.Builder!.Role<B>());
    }
    [Fact]
    public void DuplicateOwnedOptionsFailBeforeAnyServiceCallback()
    {
        var callbacks = 0;
        A.Declare = r => { r.Services(_ => callbacks++); r.Options<Probe>("A"); };
        B.Declare = r => r.Options<Probe>("B");
        var exception = Assert.Throws<CompositionException>(() => Builder().AddTroupe<Definition>());
        Assert.Equal(0, callbacks);
        Assert.Contains(exception.Manifest!.Diagnostics, d => d.Code == "TRP007" && d.Owners.Count == 2);
    }
    [Fact]
    public void ServiceCallbackFailureRetainsOriginalCauseAndPoisonsTheSession()
    {
        var original = new InvalidOperationException("service callback");
        A.Declare = r => r.Services(_ => throw original);
        var builder = Builder("a");
        var exception = Assert.Throws<CompositionException>(() => builder.AddTroupe<Definition>());
        Assert.Same(original, exception.InnerException);
        Assert.Contains(exception.Manifest!.Diagnostics, d => d.Code == "TRP017" && d.Owners.Single().Name == "a");
        Assert.Same(exception, Assert.Throws<CompositionException>(() => builder.Build()));
    }
    [Fact]
    public void FailedContributionsKeepTheirCauseAndDoNotBecomeUnconsumedErrors()
    {
        var calls = new List<string>(); var original = new InvalidOperationException("contribution failed");
        A.Declare = r => r.Contribute<List<string>>(_ => { calls.Add("a"); throw original; });
        B.Declare = r => r.Contribute<List<string>>(_ => calls.Add("b"));
        var builder = Builder(); builder.AddTroupe<Definition>();
        var exception = Assert.Throws<CompositionException>(() => builder.ApplyContributions(new List<string>()));
        Assert.Same(original, exception.InnerException); Assert.Equal(["a"], calls);
        Assert.Same(exception, Assert.Throws<CompositionException>(() => builder.ApplyContributions(new List<string>())));
        Assert.Same(exception, Assert.Throws<CompositionException>(() => builder.Build()));
        Assert.DoesNotContain(exception.Manifest!.Diagnostics, d => d.Code == "TRP008");
    }
    [Fact]
    public async Task GenericHostStartFailsWhenAnotherProviderFactoryBuiltTheHost()
    {
        var checks = 0; A.Declare = r => r.Check((_, _) => { checks++; return Task.CompletedTask; });
        var builder = Builder("a"); builder.AddTroupe<Definition>();
        builder.ConfigureContainer(new DefaultServiceProviderFactory());
        using var host = builder.Build();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("TRP018", failure.ToString());
        Assert.Equal(0, checks);
    }
    [Fact]
    public void CompletedServiceAndBuilderCallbacksAreReleasedWhileHostRemainsAlive()
    {
        using var host = CreateHostWithCapturedCallbacks(out var captured);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(captured.IsAlive); GC.KeepAlive(host);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IHost CreateHostWithCapturedCallbacks(out WeakReference captured)
    {
        var marker = new object(); captured = new(marker);
        A.Declare = r =>
        {
            r.Services(_ => GC.KeepAlive(marker));
            r.Contribute<List<string>>(_ => GC.KeepAlive(marker));
        };
        var builder = Builder("a"); builder.AddTroupe<Definition>(); builder.ApplyContributions(new List<string>());
        A.Declare = _ => { };
        return builder.Build();
    }
    [Fact]
    public void ContributionsCannotBeAppliedAfterBuild()
    {
        A.Declare = r => { r.Contribute<List<string>>(target => target.Add("a")); r.Contribute<HashSet<string>>(target => target.Add("a")); };
        var builder = Builder("a"); builder.AddTroupe<Definition>(); builder.ApplyContributions(new List<string>());
        var failure = Assert.Throws<CompositionException>(() => builder.Build());
        Assert.Contains(failure.Diagnostics, d => d.Code == "TRP008" && d.Message.Contains("HashSet"));
        var target = new HashSet<string>();
        Assert.Same(failure, Assert.Throws<CompositionException>(() => builder.ApplyContributions(target)));
        Assert.Empty(target);
    }
    [Fact]
    public async Task CompletedStartupChecksReleaseTheirCapturedState()
    {
        using var host = CreateHostWithCapturedCheck(out var captured);
        await host.StartAsync(TestContext.Current.CancellationToken);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(captured.IsAlive); GC.KeepAlive(host);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IHost CreateHostWithCapturedCheck(out WeakReference captured)
    {
        var marker = new object(); captured = new(marker);
        A.Declare = r => r.Check((_, _) => { GC.KeepAlive(marker); return Task.CompletedTask; });
        var builder = Builder("a"); builder.AddTroupe<Definition>(); A.Declare = _ => { };
        return builder.Build();
    }
    [Fact]
    public async Task CanceledChecksLeaveAFailedSessionWithTheirCause()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        A.Declare = r => r.Check(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        var builder = Builder("a"); builder.AddTroupe<Definition>(); using var host = builder.Build();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var start = host.StartAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        var failure = Assert.Throws<CompositionException>(() => builder.FinalizeForTest());
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
        Assert.Contains(failure.Manifest!.Diagnostics, d => d.Owners.Single().Name == "a" && d.Message.Contains("canceled"));
    }
    [Fact]
    public void DiagnosticDeduplicationUsesOwnerContents()
    {
        var builder = Builder("a"); var manifest = builder.AddTroupe<DuplicateDiagnosticsDefinition>();
        Assert.Single(manifest.Diagnostics, d => d.Code == "APP001");
    }
    public sealed class DuplicateDiagnosticsDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<A>().Rule(new RepeatDiagnostic()); }
    private sealed class RepeatDiagnostic : ICompositionRule
    {
        public IEnumerable<CompositionDiagnostic> Evaluate(CompositionManifest manifest) =>
        [new("APP001", DiagnosticSeverity.Advisory, "same", [new(OwnerKind.Application, "application")]),
         new("APP001", DiagnosticSeverity.Advisory, "same", [new(OwnerKind.Application, "application")])];
    }
    public sealed class RetainedDefinition : ICompositionDefinition
    {
        internal static ICompositionDefinitionBuilder? Builder;
        public void Define(ICompositionDefinitionBuilder d) { Builder = d; d.Role<A>(); }
    }
    public sealed class Probe;
}
