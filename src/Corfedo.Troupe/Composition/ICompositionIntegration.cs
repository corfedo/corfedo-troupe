namespace Corfedo.Troupe;

// The companion web package participates in validation, pipeline consumption and snapshot publication.
internal interface ICompositionIntegration : ICompositionRule
{
    IReadOnlyList<CompositionOwner> PipelineOwners { get; }
    object Snapshot();
    void ReleaseCallbacks();
}

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
