using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Corfedo.Troupe.AspNetCore;

internal static class EndpointRules
{
    internal static IEnumerable<CompositionDiagnostic> Evaluate(IReadOnlyList<EndpointEntry> endpoints)
    {
        foreach (var named in endpoints.Select(e => (Entry: e, Name: e.Endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName))
                     .Where(e => e.Name is not null).GroupBy(e => e.Name, StringComparer.Ordinal))
            if (named.Count() > 1)
                yield return new("TRP101", DiagnosticSeverity.Error, $"Endpoint name {named.Key} is not globally unique.",
                    Array.AsReadOnly(named.Select(e => e.Entry.Owner).Distinct().ToArray()));
        for (var i = 0; i < endpoints.Count; i++)
            for (var j = i + 1; j < endpoints.Count; j++)
                if (endpoints[i].Endpoint is RouteEndpoint a && endpoints[j].Endpoint is RouteEndpoint b && Candidate(a, b))
                    yield return new("TRP102", DiagnosticSeverity.Advisory,
                        $"Candidate route overlap: {a.RoutePattern.RawText} and {b.RoutePattern.RawText}. Custom matcher policies require HTTP verification.",
                        Array.AsReadOnly(new[] { endpoints[i].Owner, endpoints[j].Owner }.Distinct().ToArray()));
    }
    private static bool Candidate(RouteEndpoint a, RouteEndpoint b)
    {
        if (a.Order != b.Order || a.RoutePattern.InboundPrecedence != b.RoutePattern.InboundPrecedence) return false;
        var am = a.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        var bm = b.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        if (am is { Count: > 0 } && bm is { Count: > 0 } && !am.Intersect(bm, StringComparer.OrdinalIgnoreCase).Any()) return false;
        var ah = a.Metadata.GetMetadata<IHostMetadata>()?.Hosts;
        var bh = b.Metadata.GetMetadata<IHostMetadata>()?.Hosts;
        // Exact host sets can be proven disjoint. Wildcard policies remain candidate findings.
        if (ah is { Count: > 0 } && bh is { Count: > 0 } && ah.All(h => !h.Contains('*')) && bh.All(h => !h.Contains('*')) &&
            !ah.Intersect(bh, StringComparer.OrdinalIgnoreCase).Any()) return false;
        var ap = a.RoutePattern.PathSegments; var bp = b.RoutePattern.PathSegments;
        if (ap.Count != bp.Count) return false;
        for (var i = 0; i < ap.Count; i++)
        {
            if (ap[i].Parts.Count == 1 && bp[i].Parts.Count == 1)
            {
                if (ap[i].Parts[0] is RoutePatternLiteralPart al && bp[i].Parts[0] is RoutePatternLiteralPart bl && !string.Equals(al.Content, bl.Content, StringComparison.OrdinalIgnoreCase)) return false;
                if (ap[i].Parts[0] is RoutePatternParameterPart av && bp[i].Parts[0] is RoutePatternParameterPart bv && DisjointConstraints(av, bv)) return false;
            }
        }
        return true;
    }
    private static bool DisjointConstraints(RoutePatternParameterPart a, RoutePatternParameterPart b)
    {
        var ac = a.ParameterPolicies.Select(p => p.Content).ToArray();
        var bc = b.ParameterPolicies.Select(p => p.Content).ToArray();
        static bool Numeric(string? p) => p is "int" or "long" or "decimal" or "double" or "float";
        return ac.Contains("alpha") && bc.Any(Numeric) || bc.Contains("alpha") && ac.Any(Numeric);
    }
}
