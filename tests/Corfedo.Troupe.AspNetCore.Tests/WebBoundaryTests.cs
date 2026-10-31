using System.Runtime.CompilerServices;
using Corfedo.Troupe.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using static Corfedo.Troupe.AspNetCore.Tests.WebTests;

namespace Corfedo.Troupe.AspNetCore.Tests;

public sealed class WebBoundaryTests
{
    public WebBoundaryTests() => _ = new WebTests();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnappliedWebContributionsPreventGenericAndWebHostStartup(bool webHost)
    {
        A.Declare = r => r.Endpoints(e => e.MapGet("/", () => "ok"));
        IHostApplicationBuilder builder = webHost ? Builder("a") :
            Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        builder.Configuration["Roles"] = "a";
        builder.Configuration["Kestrel:Endpoints:public:Url"] = "http://127.0.0.1:0";
        builder.Compose<Definition>(); builder.FinalizeComposition();
        var host = builder is WebApplicationBuilder web ? web.Build() : ((HostApplicationBuilder)builder).Build();
        try
        {
            var failure = await Assert.ThrowsAsync<CompositionException>(() => host.StartAsync(TestContext.Current.CancellationToken));
            Assert.Contains(failure.Manifest!.Diagnostics, d => d.Code == "TRP105");
            Assert.Contains(failure.Diagnostics.SelectMany(d => d.Owners), o => o.Name == "a");
        }
        finally { if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync(); else host.Dispose(); }
    }
    [Fact]
    public async Task DeclaredStagesArePublishedWithoutSelectedWebCallbacks()
    {
        var builder = Builder("plain"); builder.Configuration.Sources.Clear(); builder.Configuration.AddInMemoryCollection();
        builder.Configuration["Roles"] = "plain";
        builder.Compose<StageOnlyDefinition>(); builder.FinalizeComposition(); await using var app = builder.Build();
        var runtime = app.Services.GetRequiredService<IComposition>();
        Assert.Equal(["routing", "ingress"], runtime.AspNetCore().Stages);
        app.UseComposition(); Assert.Equal(["routing", "ingress"], runtime.AspNetCore().Stages);
    }
    [Fact]
    public void StageDeclarationInstallsListenerCoverageBeforeAnyPipelineOrBuild()
    {
        var builder = Builder("plain");
        var failure = Assert.Throws<CompositionException>(() => builder.Compose<StageOnlyDefinition>());
        Assert.Contains(failure.Manifest!.Diagnostics, d => d.Code == "TRP103" && d.Message.Contains("not requested"));
    }
    [Fact]
    public async Task MissingRootRoutingIncludesTheDiagnosticInItsManifest()
    {
        var builder = Builder("a"); builder.Compose<NoRoutingDefinition>(); await using var app = builder.Build();
        var failure = Assert.Throws<CompositionException>(() => app.UseComposition());
        Assert.Contains(failure.Manifest!.Diagnostics, d => d.Code == "TRP100" && d.Message.Contains("UseRouting"));
        Assert.Same(failure, Assert.Throws<CompositionException>(() => builder.FinalizeComposition()));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MiddlewareAndEndpointCallbackFailuresPreserveTheirOwnerAndOriginalCause(bool endpoint)
    {
        var original = new InvalidOperationException("web callback");
        A.Declare = r =>
        {
            if (endpoint) r.Endpoints(_ => throw original);
            else r.Use("ingress", _ => throw original);
        };
        var builder = Builder("a"); builder.Compose<Definition>(); await using var app = builder.Build();
        var failure = Assert.Throws<CompositionException>(() => app.UseComposition());
        Assert.Same(original, failure.InnerException);
        Assert.Contains(failure.Manifest!.Diagnostics, d => d.Code == "TRP017" && d.Owners.Single().Name == "a");
        Assert.Same(failure, Assert.Throws<CompositionException>(() => app.UseComposition()));
    }
    [Theory]
    [InlineData("Use")]
    [InlineData("Endpoints")]
    [InlineData("KestrelEndpoint")]
    public void LateWebDeclarationsAreRejectedByTheClosedContributor(string declaration)
    {
        A.Declare = r => r.Services(_ =>
        {
            switch (declaration)
            {
                case "Use": r.Use("ingress", _ => { }); break;
                case "Endpoints": r.Endpoints(_ => { }); break;
                case "KestrelEndpoint": r.KestrelEndpoint("other"); break;
            }
        });
        var failure = Assert.Throws<CompositionException>(() => Builder("a").Compose<Definition>());
        Assert.Contains(failure.Manifest!.Diagnostics, d => d.Code == "TRP015" && d.Owners.Single().Name == "a");
    }
    [Fact]
    public async Task PipelineApplicationCannotBeRepeated()
    {
        var calls = 0; A.Declare = r => r.Endpoints(e => { calls++; e.MapGet("/", () => "ok"); });
        var builder = Builder("a"); builder.Compose<Definition>(); await using var app = builder.Build(); app.UseComposition();
        var failure = Assert.Throws<CompositionException>(() => app.UseComposition());
        Assert.Contains(failure.Manifest!.Diagnostics, d => d.Code == "TRP016"); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task RootRoutingMarkerBoundaryIsPinnedToAspNetCore10()
    {
        var builder = Builder("a"); await using var app = builder.Build();
        var application = (IApplicationBuilder)app;
        Assert.False(application.Properties.ContainsKey("__EndpointRouteBuilder"));
        application.UseRouting();
        Assert.True(application.Properties.ContainsKey("__EndpointRouteBuilder"));
        Assert.StartsWith("10.0.", typeof(WebApplication).Assembly.GetName().Version!.ToString());
    }
    [Fact]
    public async Task CompletedWebDeclarationCallbacksAreReleasedBeforeStart()
    {
        await using var app = CreateMappedApplication(out var captured);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(captured.IsAlive); GC.KeepAlive(app);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WebApplication CreateMappedApplication(out WeakReference captured)
    {
        var marker = new object(); captured = new(marker);
        A.Declare = r =>
        {
            r.Use("ingress", _ => GC.KeepAlive(marker));
            r.Endpoints(e => { GC.KeepAlive(marker); e.MapGet("/", () => "ok"); });
        };
        var builder = Builder("a"); builder.Compose<Definition>(); A.Declare = _ => { };
        var app = builder.Build(); app.UseComposition(); return app;
    }
    public sealed class StageOnlyDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Role<Plain>(); d.Stages("routing", "ingress"); }
    }
    [Role("plain")] public sealed class Plain : IRole { public void Compose(IRoleBuilder r) { } }
}
