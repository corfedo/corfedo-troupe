using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe;

internal static class CoreRules
{
    internal static IEnumerable<CompositionDiagnostic> Evaluate(CompositionSession session, CompositionManifest manifest)
    {
        var active = manifest.Services.Where(s => s.Active).ToArray();
        foreach (var slot in active.GroupBy(s => (s.ServiceType, s.Key)))
        {
            var owners = slot.Select(s => s.Owner).Distinct().ToArray();
            if (session.Exclusive.Contains(slot.Key) && slot.Count() > 1)
                yield return Diagnostic("TRP004", DiagnosticSeverity.Error, $"Exclusive service slot {slot.Key} has {slot.Count()} registrations.", owners);
            else if (owners.Length > 1 && !session.Multiple.Contains(slot.Key.ServiceType) && !Excluded(slot.Key.ServiceType))
                yield return Diagnostic("TRP001", DiagnosticSeverity.Advisory, $"Service slot {slot.Key} repeats across {string.Join(", ", owners.Select(o => o.Name))}.", owners);
        }
        var hosted = active.Where(s => s.ServiceType == typeof(IHostedService) && s.Key is null).ToArray();
        foreach (var identity in hosted.Where(s => s.ImplementationType is not null).GroupBy(s => s.ImplementationType))
            if (identity.Count() > 1)
                yield return Diagnostic("TRP002", DiagnosticSeverity.Error, $"Hosted service {identity.Key} is registered more than once.", identity.Select(s => s.Owner).Distinct().ToArray());
        foreach (var unknown in hosted.Where(s => s.IsFactory))
            yield return Diagnostic("TRP006", DiagnosticSeverity.Advisory, $"Hosted service occurrence {unknown.Id} has unknown factory implementation identity.", [unknown.Owner]);
    }
    private static bool Excluded(Type type)
    {
        if (type == typeof(IHostedService) || type.FullName is "Microsoft.AspNetCore.Hosting.IStartupFilter" or "Microsoft.Extensions.Logging.ILoggerProvider") return true;
        return type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IConfigureOptions<>) ||
            type.GetGenericTypeDefinition() == typeof(IPostConfigureOptions<>) ||
            type.GetGenericTypeDefinition() == typeof(IValidateOptions<>) ||
            type.GetGenericTypeDefinition() == typeof(IOptionsChangeTokenSource<>));
    }
    private static CompositionDiagnostic Diagnostic(string code, DiagnosticSeverity severity, string message, CompositionOwner[] owners) =>
        new(code, severity, message, Array.AsReadOnly(owners));
}
