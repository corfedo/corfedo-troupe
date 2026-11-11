using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe;

internal static class CoreRules
{
    internal static IEnumerable<CompositionDiagnostic> Evaluate(CompositionSession session, CompositionManifest manifest)
    {
        foreach (var read in manifest.Configuration.Where(r => Needed(r, session.Runtime)))
            if (!session.Configuration.GetSection(read.Section).Exists())
                yield return Diagnostic("TRP005", DiagnosticSeverity.Error, read.UnlessComposed is { } component
                    ? $"{read.Owner.Name}: section {read.Section} is required because {component.FullName ?? component.Name} is not composed in this process."
                    : $"{read.Owner.Name}: required section {read.Section} is missing.", [read.Owner]);
        var active = manifest.Services.Where(s => s.Active).ToArray();
        var consumers = new Lazy<ConsumerIndex>(() => new(active));
        foreach (var slot in active.GroupBy(s => (s.ServiceType, s.Key)))
        {
            var owners = slot.Select(s => s.Owner).Distinct().ToArray();
            if (session.IsExclusive(slot.Key.ServiceType, slot.Key.Key) && slot.Count() > 1)
                yield return Diagnostic("TRP004", DiagnosticSeverity.Error, $"Exclusive service slot {slot.Key} has {slot.Count()} registrations.", owners);
            else if (owners.Length > 1 && !session.AllowsMultiple(slot.Key.ServiceType) && !Excluded(slot.Key.ServiceType) &&
                     !consumers.Value.OnlyEnumerable(slot.Key.ServiceType))
                yield return Diagnostic("TRP001", DiagnosticSeverity.Advisory, $"Service slot {slot.Key} repeats across {string.Join(", ", owners.Select(o => o.Name))}.", owners);
        }
        // The application removed or replaced a contributor's occurrence: Program.cs code after AddTroupe or an adapter.
        foreach (var lost in manifest.Services.Where(s => s.RemovedBy?.Kind == OwnerKind.Application && s.Owner.Kind is OwnerKind.Component or OwnerKind.Role)
                     .GroupBy(s => s.Owner))
            yield return Diagnostic("TRP021", DiagnosticSeverity.Error, $"application removed or replaced {string.Join(", ", lost.Select(s => $"{s.ServiceType} occurrence {s.Id}"))} " +
                $"owned by {lost.Key.Name}. Change the registration in {lost.Key.Name}, or use a test override in tests.", [ServiceLedger.Application, lost.Key]);
        var hosted = active.Where(s => s.ServiceType == typeof(IHostedService) && s.Key is null).ToArray();
        foreach (var identity in hosted.Where(s => s.ImplementationType is not null).GroupBy(s => s.ImplementationType))
            if (identity.Count() > 1)
                yield return Diagnostic("TRP002", DiagnosticSeverity.Error, $"Hosted service {identity.Key} is registered more than once.", identity.Select(s => s.Owner).Distinct().ToArray());
        foreach (var unknown in hosted.Where(s => s.IsFactory))
            yield return Diagnostic("TRP006", DiagnosticSeverity.Advisory, $"Hosted service occurrence {unknown.Id} has unknown factory implementation identity.", [unknown.Owner]);
    }
    // A section is needed when its read is required, or when the component that makes it optional is not composed.
    internal static bool Needed(ConfigurationRead read, RuntimeComposition composition) =>
        read.Required || read.UnlessComposed is { } absent && !composition.Includes(absent);
    private static bool Excluded(Type type)
    {
        if (type == typeof(IHostedService) || type.FullName is "Microsoft.AspNetCore.Hosting.IStartupFilter" or "Microsoft.Extensions.Logging.ILoggerProvider") return true;
        return type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(IConfigureOptions<>) ||
            type.GetGenericTypeDefinition() == typeof(IPostConfigureOptions<>) ||
            type.GetGenericTypeDefinition() == typeof(IValidateOptions<>) ||
            type.GetGenericTypeDefinition() == typeof(IOptionsChangeTokenSource<>));
    }
    // How the public constructors of active implementation types take each service type. A repeated slot whose visible
    // consumers all take IEnumerable<T> is an extension point; a slot with a single consumer or no visible consumer stays reported.
    // Factory registrations, GetService calls and handler parameters are not visible.
    private sealed class ConsumerIndex
    {
        private readonly HashSet<Type> single = [];
        private readonly HashSet<Type> enumerable = [];
        internal ConsumerIndex(IEnumerable<ServiceRegistration> active)
        {
            foreach (var type in active.Select(s => s.ImplementationType).OfType<Type>().Distinct())
                foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                    if (parameter.IsGenericType && parameter.GetGenericTypeDefinition() == typeof(IEnumerable<>)) enumerable.Add(parameter.GetGenericArguments()[0]);
                    else single.Add(parameter);
        }
        internal bool OnlyEnumerable(Type type) => enumerable.Contains(type) && !single.Contains(type);
    }
    private static CompositionDiagnostic Diagnostic(string code, DiagnosticSeverity severity, string message, CompositionOwner[] owners) =>
        new(code, severity, message, Array.AsReadOnly(owners));
}
