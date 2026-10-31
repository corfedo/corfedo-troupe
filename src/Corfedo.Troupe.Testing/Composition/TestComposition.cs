using Corfedo.Troupe.AspNetCore;
using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe.Testing;

/// <summary>Composes a component slice from a test-owned root contributor instead of selected roles.</summary>
/// <remarks>
/// The root records <see cref="OwnerKind.Test"/> named <c>test:&lt;name&gt;</c> and declares like a role: Requires, Services, Options,
/// Contribute, Use and Endpoints. It depends on every component it requires, so it runs last in each stage and registers after
/// them. The manifest has no profile and no roles. Composition does not finalize: apply adapters, then call FinalizeComposition.
/// </remarks>
public static class TestComposition
{
    /// <summary>Composes the root's requirement closure with the stages and rules of <typeparamref name="TDefinition"/>, ignoring its roles and profiles.</summary>
    /// <param name="builder">A builder that has not composed.</param><param name="root">The test root's declarations.</param>
    /// <param name="name">The test owner name.</param>
    /// <param name="withinProfile">A declared profile that must compose every component the root requires; otherwise composition fails with TRP020.</param>
    /// <returns>The validated manifest.</returns>
    public static CompositionManifest ComposeForTest<TDefinition>(this IHostApplicationBuilder builder,
        Action<IRoleBuilder> root, string name = "test", string? withinProfile = null)
        where TDefinition : ICompositionDefinition, new()
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Compose(builder, DefinitionBuilder.Create<TDefinition>, root, name, withinProfile);
    }

    /// <summary>Composes the root's requirement closure with the supplied application stages and no rules.</summary>
    /// <param name="builder">A builder that has not composed.</param><param name="root">The test root's declarations.</param>
    /// <param name="stages">The middleware stage order, as declared by Stages; empty for a host without middleware.</param>
    /// <param name="name">The test owner name.</param>
    /// <returns>The validated manifest.</returns>
    public static CompositionManifest ComposeForTest(this IHostApplicationBuilder builder,
        Action<IRoleBuilder> root, string[] stages, string name = "test")
    {
        ArgumentNullException.ThrowIfNull(root); ArgumentNullException.ThrowIfNull(stages); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var recorded = (string[])stages.Clone();
        return Compose(builder, () => DefinitionBuilder.Create(null, d => d.Stages(recorded)), root, name, null);
    }

    private static CompositionManifest Compose(IHostApplicationBuilder builder, Func<DefinitionBuilder> define,
        Action<IRoleBuilder> root, string name, string? withinProfile) => CompositionExtensions.Compose(builder, () =>
    {
        var definition = define();
        var owner = CompositionSession.TestOwner(name);
        var nodes = Graph.Resolve([(owner, node => root(node))]);
        try { if (withinProfile is not null) Graph.RequireWithinProfile(definition, withinProfile, nodes.Single(n => n.Owner == owner)); }
        catch { foreach (var node in nodes) node.Release(); throw; }
        return (definition, null, [], nodes);
    });
}
