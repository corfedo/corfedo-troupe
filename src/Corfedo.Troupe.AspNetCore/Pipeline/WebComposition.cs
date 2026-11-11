using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe.AspNetCore;

internal sealed class WebComposition : ICompositionIntegration
{
    private readonly CompositionSession session;
    private readonly string[] stages;
    private readonly List<(CompositionOwner Owner, string Stage, Action<IApplicationBuilder> Configure)> middleware = [];
    private readonly List<(CompositionOwner Owner, Action<IEndpointRouteBuilder> Map)> mappings = [];
    private readonly List<EndpointEntry> endpoints = [];
    private readonly List<CompositionOwner> pipelineOwners = [];
    private (Type[] Served, Type[] Unserved) controllers = ([], []);

    internal WebComposition(CompositionSession session, string[] stages)
    { this.session = session; this.stages = (string[])stages.Clone(); Listeners = new(session.Configuration); }
    internal KestrelComposition Listeners { get; }
    internal ComposedControllers.ClaimSet Controllers { get; } = new();
    public IReadOnlyList<CompositionOwner> PipelineOwners => pipelineOwners.AsReadOnly();

    internal void AddMiddleware(CompositionOwner owner, string stage, Action<IApplicationBuilder> configure)
    { middleware.Add((owner, stage, configure)); AddOwner(owner); }
    internal void AddEndpoints(CompositionOwner owner, Action<IEndpointRouteBuilder> map)
    { mappings.Add((owner, map)); AddOwner(owner); }
    private void AddOwner(CompositionOwner owner) { if (!pipelineOwners.Contains(owner)) pipelineOwners.Add(owner); }

    // Every MVC process of a definition that declares controller claims filters controllers, wherever MVC was registered.
    public void Prepare(IServiceCollection services)
    {
        if (!Controllers.Declared) return;
        if (services.LastOrDefault(d => d.ServiceType == typeof(ApplicationPartManager))?.ImplementationInstance is ApplicationPartManager manager)
            Controllers.Install(manager);
        else if (Controllers.Claims.Count > 0)
            throw session.Fail("TRP107", "Controller claims require MVC: call AddControllers, AddControllersWithViews or AddRazorPages.",
                Controllers.Claims.Select(c => c.Owner).Distinct().ToArray());
    }

    internal void Apply(WebApplication application)
    {
        foreach (var stage in stages)
            foreach (var contribution in middleware.Where(m => m.Stage == stage))
                session.RunPipelineCallback(contribution.Owner, () => contribution.Configure(application));
        if (mappings.Count > 0 && !RoutingBoundary.HasRootRouting(application))
            throw session.Fail("TRP100", "Endpoint composition requires an explicit root UseRouting callback in an application-declared stage.", pipelineOwners.ToArray());
        var sources = ((IEndpointRouteBuilder)application).DataSources;
        // Data sources build request delegates when enumerated, so each source is enumerated under the owner whose map
        // callback added it; sources the application mapped directly stay application-owned.
        var added = new Dictionary<EndpointDataSource, CompositionOwner>(ReferenceEqualityComparer.Instance);
        foreach (var (owner, map) in mappings)
            session.RunPipelineCallback(owner, () =>
            {
                var before = sources.ToHashSet(ReferenceEqualityComparer.Instance);
                map(application.MapGroup("").WithMetadata(new EndpointOwner(owner)));
                foreach (var source in sources.Where(s => !before.Contains(s))) added.TryAdd(source, owner);
            });
        foreach (var source in sources.ToArray())
            session.RunPipelineCallback(added.GetValueOrDefault(source, ServiceLedger.Application), () => endpoints.AddRange(source.Endpoints
                .Select(e => new EndpointEntry(e, e.Metadata.GetMetadata<EndpointOwner>()?.Owner ?? ServiceLedger.Application))));
        controllers = Controllers.Inventory(application.Services, endpoints);
    }
    public IEnumerable<CompositionDiagnostic> Evaluate(CompositionManifest manifest)
    {
        foreach (var contribution in middleware.Where(m => !stages.Contains(m.Stage, StringComparer.Ordinal)))
            yield return new("TRP100", DiagnosticSeverity.Error, $"{contribution.Owner.Name} uses undeclared stage {contribution.Stage}.", Array.AsReadOnly(new[] { contribution.Owner }));
        foreach (var diagnostic in Listeners.Evaluate()) yield return diagnostic;
        foreach (var diagnostic in EndpointRules.Evaluate(endpoints)) yield return diagnostic;
        if (!Controllers.Declared)
            foreach (var owner in Controllers.Claims.Select(c => c.Owner).Distinct())
                yield return new("TRP107", DiagnosticSeverity.Error, $"{owner.Name} claims controllers; declare ControllerClaims() on the definition.",
                    Array.AsReadOnly(new[] { owner }));
        foreach (var (first, second) in ComposedControllers.Overlaps(Controllers.Claims)) yield return ComposedControllers.Overlap(first, second);
    }
    public object Snapshot() => new AspNetCompositionManifest(Array.AsReadOnly((string[])stages.Clone()),
        Array.AsReadOnly(endpoints.ToArray()), Listeners.Snapshot(), session.ActionsExecuted,
        Array.AsReadOnly(controllers.Served), Array.AsReadOnly(controllers.Unserved), Array.AsReadOnly(Controllers.Claims.ToArray()));
    public void ReleaseCallbacks() { middleware.Clear(); mappings.Clear(); }
}
