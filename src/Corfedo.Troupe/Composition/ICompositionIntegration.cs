using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe;

// The companion web package participates in validation, pipeline consumption and snapshot publication.
internal interface ICompositionIntegration : ICompositionRule
{
    IReadOnlyList<CompositionOwner> PipelineOwners { get; }
    // Runs in Build with the final service collection, before the provider exists.
    void Prepare(IServiceCollection services);
    object Snapshot();
    // The named listeners the composition requests, and the configured listeners no contributor requests.
    ListenerState Listeners();
    void ReleaseCallbacks();
}

internal sealed record ListenerState(IReadOnlyList<ListenerRequirement> Requested, IReadOnlyList<string> Unrequested)
{
    internal static readonly ListenerState None = new([], []);
}
// Configured: the process's configuration has an endpoint with the listener's name.
internal sealed record ListenerRequirement(string Name, IReadOnlyList<CompositionOwner> Owners, bool Configured);

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
