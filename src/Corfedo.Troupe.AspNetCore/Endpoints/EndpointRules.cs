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
        foreach (var (i, j) in CandidatePairs(endpoints))
        {
            var a = (RouteEndpoint)endpoints[i].Endpoint; var b = (RouteEndpoint)endpoints[j].Endpoint;
            yield return new("TRP102", DiagnosticSeverity.Advisory,
                $"Candidate route overlap: {a.RoutePattern.RawText} and {b.RoutePattern.RawText}. Custom matcher policies require HTTP verification.",
                Array.AsReadOnly(new[] { endpoints[i].Owner, endpoints[j].Owner }.Distinct().ToArray()));
        }
    }
    // Only routes with equal order, precedence, segment count and single-literal segment text (ignoring case) can be candidates, so pairs are
    // compared within those groups. Equal precedence places single-literal segments at the same positions in both routes.
    private static List<(int, int)> CandidatePairs(IReadOnlyList<EndpointEntry> endpoints)
    {
        var groups = new Dictionary<RouteShape, List<int>>();
        for (var i = 0; i < endpoints.Count; i++)
            if (endpoints[i].Endpoint is RouteEndpoint route)
            {
                var shape = RouteShape.Of(route);
                if (!groups.TryGetValue(shape, out var group)) groups[shape] = group = [];
                group.Add(i);
            }
        var pairs = new List<(int, int)>();
        foreach (var group in groups.Values)
            for (var x = 0; x < group.Count; x++)
                for (var y = x + 1; y < group.Count; y++)
                    if (Candidate((RouteEndpoint)endpoints[group[x]].Endpoint, (RouteEndpoint)endpoints[group[y]].Endpoint))
                        pairs.Add((group[x], group[y]));
        pairs.Sort();
        return pairs;
    }
    private readonly record struct RouteShape(int Order, decimal Precedence, int Segments, string Literals)
    {
        internal static RouteShape Of(RouteEndpoint route) => new(route.Order, route.RoutePattern.InboundPrecedence, route.RoutePattern.PathSegments.Count,
            string.Join('/', route.RoutePattern.PathSegments.Select(s => s.Parts is [RoutePatternLiteralPart literal] ? literal.Content.ToUpperInvariant() : "")));
    }
    private static bool Candidate(RouteEndpoint a, RouteEndpoint b)
    {
        if (a.Order != b.Order || a.RoutePattern.InboundPrecedence != b.RoutePattern.InboundPrecedence) return false;
        var am = a.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        var bm = b.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        if (am is { Count: > 0 } && bm is { Count: > 0 } && !am.Any(m => bm.Contains(m, StringComparer.OrdinalIgnoreCase))) return false;
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
