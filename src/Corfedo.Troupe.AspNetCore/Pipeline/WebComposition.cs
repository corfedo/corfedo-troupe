using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Corfedo.Troupe.AspNetCore;

internal sealed class WebComposition : ICompositionIntegration
{
    private readonly CompositionSession session;
    private readonly string[] stages;
    private readonly List<(CompositionOwner Owner, string Stage, Action<IApplicationBuilder> Configure)> middleware = [];
    private readonly List<(CompositionOwner Owner, Action<IEndpointRouteBuilder> Map)> mappings = [];
    private readonly List<EndpointEntry> endpoints = [];
    private readonly List<CompositionOwner> pipelineOwners = [];

    internal WebComposition(CompositionSession session, string[] stages)
    { this.session = session; this.stages = (string[])stages.Clone(); Listeners = new(session.Configuration); }
    internal KestrelComposition Listeners { get; }
    public IReadOnlyList<CompositionOwner> PipelineOwners => pipelineOwners.AsReadOnly();

    internal void AddMiddleware(CompositionOwner owner, string stage, Action<IApplicationBuilder> configure)
    { middleware.Add((owner, stage, configure)); AddOwner(owner); }
    internal void AddEndpoints(CompositionOwner owner, Action<IEndpointRouteBuilder> map)
    { mappings.Add((owner, map)); AddOwner(owner); }
    private void AddOwner(CompositionOwner owner) { if (!pipelineOwners.Contains(owner)) pipelineOwners.Add(owner); }

    internal void Apply(WebApplication application)
    {
        foreach (var stage in stages)
            foreach (var contribution in middleware.Where(m => m.Stage == stage))
                session.RunPipelineCallback(contribution.Owner, () => contribution.Configure(application));
        if (mappings.Count > 0 && !RoutingBoundary.HasRootRouting(application))
            throw session.Fail("TRP100", "Endpoint composition requires an explicit root UseRouting callback in an application-declared stage.", pipelineOwners.ToArray());
        foreach (var (owner, map) in mappings)
            session.RunPipelineCallback(owner, () => map(application.MapGroup("").WithMetadata(new EndpointOwner(owner))));
        session.RunPipelineCallback(ServiceLedger.Application, () => endpoints.AddRange(
            ((IEndpointRouteBuilder)application).DataSources.SelectMany(d => d.Endpoints)
                .Select(e => new EndpointEntry(e, e.Metadata.GetMetadata<EndpointOwner>()?.Owner ?? ServiceLedger.Application))));
    }
    public IEnumerable<CompositionDiagnostic> Evaluate(CompositionManifest manifest)
    {
        foreach (var contribution in middleware.Where(m => !stages.Contains(m.Stage, StringComparer.Ordinal)))
            yield return new("TRP100", DiagnosticSeverity.Error, $"{contribution.Owner.Name} uses undeclared stage {contribution.Stage}.", Array.AsReadOnly(new[] { contribution.Owner }));
        foreach (var diagnostic in Listeners.Evaluate()) yield return diagnostic;
        foreach (var diagnostic in EndpointRules.Evaluate(endpoints)) yield return diagnostic;
    }
    public object Snapshot() => new AspNetCompositionManifest(Array.AsReadOnly((string[])stages.Clone()),
        Array.AsReadOnly(endpoints.ToArray()), Listeners.Snapshot());
    public void ReleaseCallbacks() { middleware.Clear(); mappings.Clear(); }
}
