using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Corfedo.Troupe.AspNetCore;

internal sealed class WebComposition : ICompositionRule
{
    internal readonly CompositionSession Session;
    internal readonly string[] Stages;
    internal readonly List<(CompositionOwner Owner, string Stage, Action<IApplicationBuilder> Configure)> Middleware = [];
    internal readonly List<(CompositionOwner Owner, Action<IEndpointRouteBuilder> Map)> Mappings = [];
    internal readonly Dictionary<string, List<CompositionOwner>> Requested = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, CompositionOwner> Callbacks = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<EndpointEntry> Endpoints = [];
    internal bool Applied;

    private WebComposition(CompositionSession session)
    {
        Session = session;
        Stages = session.Definition.Extensions.TryGetValue(typeof(WebComposition), out var stages) ? (string[])stages : [];
        session.InternalRules.Add(this);
    }
    internal static WebComposition Get(CompositionSession session)
    {
        if (!session.Extensions.TryGetValue(typeof(WebComposition), out var value))
        { value = new WebComposition(session); session.Extensions.Add(typeof(WebComposition), value); }
        return (WebComposition)value;
    }
    public IEnumerable<CompositionDiagnostic> Evaluate(CompositionManifest manifest)
    {
        foreach (var middleware in Middleware.Where(m => !Stages.Contains(m.Stage, StringComparer.Ordinal)))
            yield return new("TRP100", DiagnosticSeverity.Error, $"{middleware.Owner.Name} uses undeclared stage {middleware.Stage}.", Array.AsReadOnly(new[] { middleware.Owner }));
        var configured = Session.Builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in Requested)
            if (!configured.Contains(requested.Key))
                yield return new("TRP103", DiagnosticSeverity.Error, $"Requested Kestrel endpoint {requested.Key} is missing from Kestrel:Endpoints.", Array.AsReadOnly(requested.Value.ToArray()));
        foreach (var extra in configured.Where(n => !Requested.ContainsKey(n)))
            yield return new("TRP103", DiagnosticSeverity.Error, $"Configured Kestrel endpoint {extra} is not requested by this composition.", Array.AsReadOnly(new[] { ServiceLedger.Application }));
        foreach (var diagnostic in EndpointRules.Evaluate(Endpoints)) yield return diagnostic;
    }
    internal AspNetCompositionManifest Snapshot() => new(Array.AsReadOnly((string[])Stages.Clone()),
        Array.AsReadOnly(Endpoints.ToArray()), Array.AsReadOnly(Requested.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new KestrelEndpointEntry(p.Key, Array.AsReadOnly(p.Value.ToArray()), Callbacks.GetValueOrDefault(p.Key))).ToArray()));
}
