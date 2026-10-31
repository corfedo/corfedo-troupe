using Microsoft.Extensions.Configuration;

namespace Corfedo.Troupe.AspNetCore;

internal sealed class KestrelComposition(IConfiguration configuration)
{
    private readonly Dictionary<string, List<CompositionOwner>> requested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CompositionOwner> callbacks = new(StringComparer.OrdinalIgnoreCase);

    internal void Require(string name, CompositionOwner owner)
    {
        if (!requested.TryGetValue(name, out var owners)) requested.Add(name, owners = []);
        if (!owners.Contains(owner)) owners.Add(owner);
    }
    internal void RegisterCallback(CompositionSession session, string name)
    {
        session.RequireConfiguration();
        if (!requested.ContainsKey(name)) throw session.Fail("TRP103", $"Kestrel endpoint {name} is not requested.", ServiceLedger.Application);
        if (!callbacks.TryAdd(name, ServiceLedger.Application))
            throw session.Fail("TRP104", $"Kestrel endpoint {name} already has an application callback owner; a second callback would replace its policy.", ServiceLedger.Application);
    }
    internal IEnumerable<CompositionDiagnostic> Evaluate()
    {
        var configured = configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in requested)
            if (!configured.Contains(requirement.Key))
                yield return new("TRP103", DiagnosticSeverity.Error, $"Requested Kestrel endpoint {requirement.Key} is missing from Kestrel:Endpoints.", Array.AsReadOnly(requirement.Value.ToArray()));
        foreach (var extra in configured.Where(n => !requested.ContainsKey(n)))
            yield return new("TRP103", DiagnosticSeverity.Error, $"Configured Kestrel endpoint {extra} is not requested by this composition.", Array.AsReadOnly(new[] { ServiceLedger.Application }));
    }
    internal IReadOnlyList<KestrelEndpointEntry> Snapshot() => Array.AsReadOnly(requested.OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => new KestrelEndpointEntry(p.Key, Array.AsReadOnly(p.Value.ToArray()), callbacks.GetValueOrDefault(p.Key))).ToArray());
}
