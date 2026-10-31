namespace Corfedo.Troupe.Examples.Operations;

/// <summary>
/// Checks caller declarations against the catalogue in both directions: a caller declares every owner whose
/// operations it may invoke, and every declared owner exposes something to it. Reads only the manifest.
/// </summary>
public sealed class CallsRule : ICompositionRule
{
    public IEnumerable<CompositionDiagnostic> Evaluate(CompositionManifest manifest)
    {
        var declared = manifest.Services
            .Where(s => s.Active && s.Key is Workload && s.ServiceType.IsGenericType && s.ServiceType.GetGenericTypeDefinition() == typeof(Destination<>))
            .Select(s => (Caller: (Workload)s.Key!, Owner: OperationTable.Hosts.Single(h => h.Host == s.ServiceType.GetGenericArguments()[0]).Owner, Declaring: s.Owner))
            .ToArray();
        foreach (var caller in declared.GroupBy(d => d.Caller))
        {
            var owners = caller.Select(d => d.Owner).ToHashSet();
            var declaring = caller.Select(d => d.Declaring).Distinct().ToArray();
            var called = OperationTable.Operations.Where(o => o.Callers.Contains(caller.Key)).Select(o => o.Owner).ToHashSet();
            foreach (var missing in called.Except(owners).Order())
                yield return new("OPS001", DiagnosticSeverity.Error,
                    $"{caller.Key.Name()} may invoke {missing.Name()} operations but declares no destination for {missing.Name()}.", declaring);
            foreach (var extra in owners.Except(called).Order())
                yield return new("OPS002", DiagnosticSeverity.Error,
                    $"{caller.Key.Name()} declares {extra.Name()} as a destination, but {extra.Name()} exposes no operation to it.", declaring);
        }
    }
}
