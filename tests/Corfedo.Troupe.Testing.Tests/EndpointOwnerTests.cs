using Corfedo.Troupe.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Xunit;
using static Corfedo.Troupe.Testing.Tests.TestHosts;

namespace Corfedo.Troupe.Testing.Tests.EndpointOwners;

// An endpoint mapping failure names the component whose map callback added the failing endpoint.
public sealed class EndpointOwnerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MappingFailureNamesTheSecondComponentNotTheFirst()
    {
        var result = await CompositionVerifier.VerifyAsync<TwoMappersDefinition>(Web, cancellationToken: Token);
        var diagnostic = Assert.Single(Assert.IsType<CompositionException>(Assert.Single(result.Failures).Exception).Diagnostics);
        Assert.Equal([typeof(BadMapper)], diagnostic.Owners.Select(o => o.Type));
    }

    [Fact]
    public async Task DeferredMappingFailureNamesTheComponent()
    {
        var result = await CompositionVerifier.VerifyAsync<InvalidEndpointDefinition>(Web, cancellationToken: Token);
        Assert.False(result.Succeeded);
        var diagnostic = Assert.Single(Assert.IsType<CompositionException>(Assert.Single(result.Failures).Exception).Diagnostics);
        Assert.Equal("TRP017", diagnostic.Code);
        Assert.Contains(diagnostic.Owners, o => o.Type == typeof(InvalidEndpointComponent));
    }
}

// GoodMapper maps a valid endpoint, BadMapper an endpoint whose body parameter cannot be inferred.
public sealed class GoodMapper : IComponent
{
    public void Compose(IComponentBuilder c) { c.Use("routing", a => a.UseRouting()); c.Endpoints(e => e.MapGet("/good", () => "ok")); }
}
public sealed class BadMapper : IComponent { public void Compose(IComponentBuilder c) => c.Endpoints(e => e.MapGet("/bad", (UnregisteredBody body) => body)); }
public sealed class TwoMappersDefinition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.Requires<GoodMapper>().Requires<BadMapper>(); }
}
public sealed class InvalidEndpointDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.Requires<InvalidEndpointComponent>(); } }
public sealed class InvalidEndpointComponent : IComponent
{
    public void Compose(IComponentBuilder c)
    {
        c.Use("routing", a => a.UseRouting());
        c.Endpoints(e => e.MapGet("/invalid", (UnregisteredBody body) => body));
    }
}
public sealed class UnregisteredBody { public string Value { get; set; } = ""; }
