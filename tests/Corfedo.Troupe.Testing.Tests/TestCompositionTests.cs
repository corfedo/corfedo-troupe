using Corfedo.Troupe.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Corfedo.Troupe.Testing.Tests;

public sealed class TestCompositionTests
{
    private static readonly CompositionOwner Root = new(OwnerKind.Test, "test:slice");
    private static readonly List<string> Applied = [];
    public TestCompositionTests() { Applied.Clear(); CountingRule.Calls = 0; }
    private static HostApplicationBuilder Builder()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        return builder;
    }
    private static WebApplicationBuilder WebBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
        return builder;
    }
    [Fact]
    public void RootComposesItsClosureLastWithTheDefinitionsRulesAndNoRolesOrProfile()
    {
        var builder = Builder();
        var manifest = builder.ComposeForTest<Definition>(root =>
        {
            root.Requires<Branch>();
            root.Services(services => services.AddSingleton<Probe>());
        }, "slice");
        Assert.Null(manifest.Profile); Assert.Empty(manifest.Roles);
        Assert.Equal([typeof(Store).FullName!, typeof(Branch).FullName!, Root.Name], manifest.Contributors.Select(c => c.Owner.Name));
        Assert.Equal(Root, manifest.Contributors[^1].Owner);
        Assert.Equal([new CompositionOwner(OwnerKind.Component, typeof(Branch).FullName!, typeof(Branch))], manifest.Contributors[^1].Dependencies);
        Assert.Equal(Root, Assert.Single(manifest.Services, s => s.ServiceType == typeof(Probe)).Owner);
        Assert.True(CountingRule.Calls > 0);
        builder.FinalizeComposition();
        using var host = builder.Build();
        var composition = host.Services.GetRequiredService<IComposition>();
        Assert.True(composition.Includes<Store>()); Assert.False(composition.Includes<Elsewhere>());
        Assert.Empty(composition.Roles); Assert.Null(composition.Profile);
    }
    [Fact]
    public void RootContributionsWaitForTheCallerToApplyAdaptersAndFinalize()
    {
        var unapplied = Builder();
        unapplied.ComposeForTest<Definition>(root => root.Contribute<List<string>>(target => target.Add("root")));
        Assert.Contains("TRP008", Assert.Throws<CompositionException>(() => unapplied.FinalizeComposition()).Message);
        var builder = Builder();
        builder.ComposeForTest<Definition>(root => { root.Requires<Adapter>(); root.Contribute<List<string>>(target => target.Add("root")); });
        var target = new List<string>(); builder.ApplyContributions(target);
        Assert.Equal(["adapter", "root"], target);
        Assert.All(builder.FinalizeComposition().Contributions, c => Assert.True(c.Applied));
    }
    [Fact]
    public async Task WebRootRunsLastInEachStageAndOwnsItsEndpoints()
    {
        var builder = WebBuilder();
        builder.ComposeForTest<WebDefinition>(root =>
        {
            root.Requires<Ingress>();
            root.Use("auth", _ => Applied.Add("root:auth"));
            root.Endpoints(endpoints => endpoints.MapGet("/root", () => "root"));
        }, "slice");
        builder.FinalizeComposition();
        await using var application = builder.Build();
        application.UseComposition();
        Assert.Equal(["ingress:routing", "ingress:auth", "root:auth"], Applied);
        var web = application.Services.GetRequiredService<IComposition>().AspNetCore();
        Assert.Equal(["routing", "auth"], web.Stages);
        Assert.Equal(Root, Assert.Single(web.Endpoints, e => (e.Endpoint as RouteEndpoint)?.RoutePattern.RawText == "/root").Owner);
    }
    [Fact]
    public void StagesOverloadComposesWithoutADefinition()
    {
        var builder = WebBuilder();
        var manifest = builder.ComposeForTest(root =>
        {
            root.Use("routing", application => { Applied.Add("root:routing"); application.UseRouting(); });
            root.Endpoints(endpoints => endpoints.MapGet("/", () => "ok"));
        }, ["routing"], "slice");
        Assert.Equal(Root, Assert.Single(manifest.Contributors).Owner);
        builder.FinalizeComposition();
        using var application = builder.Build();
        application.UseComposition();
        Assert.Equal(["root:routing"], Applied);
        Assert.Equal(Root, Assert.Single(application.Services.GetRequiredService<IComposition>().AspNetCore().Endpoints).Owner);
    }
    [Fact]
    public void WithinProfileRejectsComponentsTheProfileDoesNotCompose()
    {
        Builder().ComposeForTest<ProfileDefinition>(root => root.Requires<Ingress>(), withinProfile: "edge");
        var error = Assert.Throws<CompositionException>(() =>
            Builder().ComposeForTest<ProfileDefinition>(root => root.Requires<Ingress>().Requires<Elsewhere>(), "slice", "edge"));
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("TRP020", diagnostic.Code); Assert.Equal([Root], diagnostic.Owners);
        Assert.Contains(typeof(Elsewhere).FullName!, diagnostic.Message); Assert.DoesNotContain(typeof(Ingress).FullName!, diagnostic.Message);
        Assert.Contains(Assert.Throws<CompositionException>(() =>
            Builder().ComposeForTest<ProfileDefinition>(root => root.Requires<Ingress>(), withinProfile: "unknown")).Diagnostics, d => d.Code == "TRP013");
    }
    [Theory]
    [InlineData("unknown", "ComposeForTest: profile 'unknown' is not declared by")]
    [InlineData(" ", "ComposeForTest: profile ' ' is not declared by")]
    public void WithinProfileRejectsUndeclaredProfilesForTheRoot(string profile, string message)
    {
        var error = Assert.Throws<CompositionException>(() =>
            Builder().ComposeForTest<ProfileDefinition>(root => root.Requires<Ingress>(), "slice", profile));
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("TRP013", diagnostic.Code); Assert.Equal([Root], diagnostic.Owners);
        Assert.StartsWith(message, diagnostic.Message); Assert.Contains(typeof(ProfileDefinition).FullName!, diagnostic.Message);
    }
    [Fact]
    public void WithinProfileRequiresADefinitionWithProfiles()
    {
        var error = Assert.Throws<CompositionException>(() => Builder().ComposeForTest<Definition>(_ => { }, "slice", "edge"));
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("TRP013", diagnostic.Code); Assert.Equal([Root], diagnostic.Owners);
        Assert.Equal($"ComposeForTest: {typeof(Definition).FullName} declares no profiles, so withinProfile cannot be used.", diagnostic.Message);
    }
    [Fact]
    public void RootOptionsAreValidatedAndOwnedByTheRoot()
    {
        var error = Assert.Throws<CompositionException>(() => Builder().ComposeForTest<Definition>(root => root.Options<Probe>("Probe"), "slice"));
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("TRP005", diagnostic.Code); Assert.Equal([Root], diagnostic.Owners);
        Assert.Equal(Root, Assert.Single(error.Manifest!.Configuration).Owner);
    }
    [Fact]
    public void ABuilderComposesOnce()
    {
        var builder = Builder(); builder.ComposeForTest<Definition>(_ => { });
        Assert.Contains("TRP016", Assert.Throws<CompositionException>(() => builder.ComposeForTest<Definition>(_ => { })).Message);
    }
    public sealed class Definition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<Unselected>().Rule(new CountingRule()); }
    public sealed class WebDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) { d.Role<Unselected>(); d.Stages("routing", "auth"); } }
    public sealed class ProfileDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<Edge>().Role<Unselected>().Profile("edge", "edge").Profile("all", "edge", "unselected").Stages("routing", "auth"); }
    private sealed class CountingRule : ICompositionRule
    {
        internal static int Calls;
        public IEnumerable<CompositionDiagnostic> Evaluate(CompositionManifest manifest) { Calls++; return []; }
    }
    [Role("unselected")] public sealed class Unselected : IRole
    { public void Compose(IRoleBuilder r) => r.Requires<Elsewhere>(); }
    [Role("edge")] public sealed class Edge : IRole
    { public void Compose(IRoleBuilder r) => r.Requires<Ingress>(); }
    public sealed class Store : IComponent { public void Compose(IComponentBuilder c) { } }
    public sealed class Adapter : IComponent
    { public void Compose(IComponentBuilder c) => c.Contribute<List<string>>(target => target.Add("adapter")); }
    public sealed class Ingress : IComponent
    {
        public void Compose(IComponentBuilder c)
        {
            c.Requires<Store>();
            c.Use("routing", application => { Applied.Add("ingress:routing"); application.UseRouting(); });
            c.Use("auth", _ => Applied.Add("ingress:auth"));
        }
    }
    public sealed class Branch : IComponent { public void Compose(IComponentBuilder c) => c.Requires<Store>(); }
    public sealed class Elsewhere : IComponent { public void Compose(IComponentBuilder c) { } }
    public sealed class Probe;
}
