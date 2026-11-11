using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe;

// The companion web package participates in validation, pipeline consumption and snapshot publication.
internal interface ICompositionIntegration : ICompositionRule
{
    IReadOnlyList<CompositionOwner> PipelineOwners { get; }
    // Runs in Build with the final service collection, before the provider exists.
    void Prepare(IServiceCollection services);
    object Snapshot();
    // The named listeners the composition requests, with the contributors that require each one.
    IReadOnlyList<ListenerRequirement> Listeners();
    void ReleaseCallbacks();
}

internal sealed record ListenerRequirement(string Name, IReadOnlyList<CompositionOwner> Owners);

internal enum CompositionPhase
{
    Composing,
    Configuring,
    Finalized,
    ApplyingPipeline,
    PipelineApplied,
    Starting,
    Started,
    Failed
}
