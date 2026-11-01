using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe;

/// <summary>Identifies a selectable role.</summary>
/// <param name="Type">The role definition type.</param><param name="Name">The ordinal selection name.</param>
public sealed record RoleIdentity(Type Type, string Name);
/// <summary>Identifies a declared serving profile.</summary>
/// <param name="Name">The profile name.</param><param name="Roles">The ordinal, distinct role names.</param>
public sealed record ProfileIdentity(string Name, IReadOnlyList<string> Roles);
/// <summary>Distinguishes contributor, adapter and privileged test provenance.</summary>
public enum OwnerKind
{
    /// <summary>Baseline host and application adapter registrations.</summary>
    Application,
    /// <summary>The composition definition, owner of the components it requires directly.</summary>
    Definition,
    /// <summary>A selected role.</summary>
    Role,
    /// <summary>A shared component.</summary>
    Component,
    /// <summary>Test-owned service overrides and test composition roots, named <c>test:&lt;name&gt;</c>.</summary>
    Test
}
/// <summary>Identifies the source of a declaration or mutation.</summary>
/// <param name="Kind">The provenance boundary.</param><param name="Name">The diagnostic name.</param>
/// <param name="Type">The definition type, when applicable.</param>
public sealed record CompositionOwner(OwnerKind Kind, string Name, Type? Type = null);
/// <summary>Describes a contributor in canonical execution order.</summary>
/// <param name="Owner">The contributor.</param><param name="Dependencies">Its direct component dependencies.</param>
public sealed record ContributorEntry(CompositionOwner Owner, IReadOnlyList<CompositionOwner> Dependencies);
/// <summary>Describes one descriptor occurrence, including removed occurrences.</summary>
/// <param name="Id">The composition-local occurrence identity.</param><param name="Owner">The registering owner.</param>
/// <param name="ServiceType">The service type.</param><param name="Lifetime">The DI lifetime.</param>
/// <param name="Key">The service key; null denotes an unkeyed service.</param>
/// <param name="ImplementationType">The known type, or null for a factory.</param>
/// <param name="IsFactory">Whether implementation identity is unknown without factory execution.</param>
/// <param name="Active">Whether this occurrence remains registered.</param>
/// <param name="RemovedBy">The owner removing or replacing the occurrence.</param>
/// <param name="ReplacedBy">The replacement occurrence, when applicable.</param>
public sealed record ServiceRegistration(long Id, CompositionOwner Owner, Type ServiceType,
    ServiceLifetime Lifetime, object? Key, Type? ImplementationType, bool IsFactory, bool Active,
    CompositionOwner? RemovedBy, long? ReplacedBy);
/// <summary>Identifies a service collection mutation entry point.</summary>
public enum ServiceMutationKind
{
    /// <summary>Adds an occurrence.</summary>
    Add,
    /// <summary>Inserts an occurrence at an index.</summary>
    Insert,
    /// <summary>Removes a matching descriptor.</summary>
    Remove,
    /// <summary>Removes an indexed occurrence.</summary>
    RemoveAt,
    /// <summary>Replaces an indexed occurrence.</summary>
    Replace,
    /// <summary>Removes an occurrence as part of clearing the collection.</summary>
    Clear,
    /// <summary>Observes a change outside the supplied facade, with application provenance.</summary>
    ApplicationChange
}
/// <summary>Records a mutation, including rejected foreign removals.</summary>
/// <param name="Sequence">The event sequence.</param><param name="Kind">The collection operation.</param>
/// <param name="Owner">The mutating owner.</param><param name="Occurrence">The affected occurrence.</param>
/// <param name="Replacement">The replacement occurrence.</param><param name="Accepted">Whether the collection changed.</param>
public sealed record ServiceMutation(long Sequence, ServiceMutationKind Kind, CompositionOwner Owner,
    long Occurrence, long? Replacement, bool Accepted);
/// <summary>Records declared configuration intent without storing values.</summary>
/// <param name="Owner">The declaring owner.</param><param name="Section">The configuration path.</param>
/// <param name="Required">Whether the section must exist.</param><param name="OptionsType">The bound type, when applicable.</param>
/// <param name="UnlessComposed">A component whose presence in this process makes the section optional; when it is absent, the section must exist.</param>
public sealed record ConfigurationRead(CompositionOwner Owner, string Section, bool Required, Type? OptionsType, Type? UnlessComposed = null);
/// <summary>Describes a deferred builder contribution.</summary>
/// <param name="Owner">The declaring owner.</param><param name="TargetType">The exact application builder type.</param>
/// <param name="Applied">Whether the callback completed.</param>
public sealed record ContributionEntry(CompositionOwner Owner, Type TargetType, bool Applied);
/// <summary>Distinguishes potential conflicts from known errors.</summary>
public enum DiagnosticSeverity
{
    /// <summary>A bounded finding that does not prevent composition.</summary>
    Advisory,
    /// <summary>A known composition error.</summary>
    Error
}
/// <summary>Reports a stable rule code and concrete ownership context.</summary>
/// <param name="Code">The stable rule identifier.</param><param name="Severity">The certainty and failure policy.</param>
/// <param name="Message">The finding without secret configuration values.</param><param name="Owners">The implicated owners.</param>
public sealed record CompositionDiagnostic(string Code, DiagnosticSeverity Severity, string Message,
    IReadOnlyList<CompositionOwner> Owners);

/// <summary>Contains a read-only point-in-time record. Factory bodies and suppressed TryAdd attempts are unknown.</summary>
public sealed class CompositionManifest
{
    internal CompositionManifest(ProfileIdentity? profile, IReadOnlyList<RoleIdentity> roles, IReadOnlyList<ContributorEntry> contributors,
        IReadOnlyList<ServiceRegistration> services, IReadOnlyList<ServiceMutation> mutations,
        IReadOnlyList<ConfigurationRead> configuration, IReadOnlyList<ContributionEntry> contributions,
        IReadOnlyList<CompositionDiagnostic> diagnostics)
    {
        Profile = profile; Roles = roles; Contributors = contributors; Services = services; ServiceMutations = mutations;
        Configuration = configuration; Contributions = contributions; Diagnostics = diagnostics;
    }
    internal CompositionManifest WithDiagnostics(IEnumerable<CompositionDiagnostic> diagnostics) =>
        new(Profile, Roles, Contributors, Services, ServiceMutations, Configuration, Contributions,
            Array.AsReadOnly(diagnostics.Distinct(DiagnosticComparer.Instance).ToArray()));
    /// <summary>Gets the selected profile, or null when the definition declares no profiles or a test root replaces the selection.</summary>
    public ProfileIdentity? Profile { get; }
    /// <summary>Gets the selected roles; empty when a test root replaces the selection.</summary>
    public IReadOnlyList<RoleIdentity> Roles { get; }
    /// <summary>Gets the canonical dependency-first execution order.</summary>
    public IReadOnlyList<ContributorEntry> Contributors { get; }
    /// <summary>Gets all known descriptor occurrences.</summary>
    public IReadOnlyList<ServiceRegistration> Services { get; }
    /// <summary>Gets recorded collection mutations.</summary>
    public IReadOnlyList<ServiceMutation> ServiceMutations { get; }
    /// <summary>Gets declared configuration reads.</summary>
    public IReadOnlyList<ConfigurationRead> Configuration { get; }
    /// <summary>Gets deferred contribution status.</summary>
    public IReadOnlyList<ContributionEntry> Contributions { get; }
    /// <summary>Gets bounded diagnostics; absence of advisories does not prove runtime safety.</summary>
    public IReadOnlyList<CompositionDiagnostic> Diagnostics { get; }

    /// <summary>Tests whether the component is composed in this snapshot.</summary>
    public bool Composes<TComponent>() where TComponent : IComponent =>
        Contributors.Any(c => c.Owner.Kind == OwnerKind.Component && c.Owner.Type == typeof(TComponent));
    /// <summary>Gets the shortest requirement chain from the definition, a role or the test root to the component.</summary>
    /// <remarks>The chain starts at the definition, role or test root and ends at the component; it is empty when the component is not composed. Ties follow ordinal names.</remarks>
    public IReadOnlyList<CompositionOwner> PathTo<TComponent>() where TComponent : IComponent
    {
        var entries = Contributors.ToDictionary(c => c.Owner);
        var previous = new Dictionary<CompositionOwner, CompositionOwner?>();
        var pending = new Queue<CompositionOwner>();
        foreach (var start in Contributors.Select(c => c.Owner).Where(o => o.Kind is OwnerKind.Definition or OwnerKind.Role or OwnerKind.Test).OrderBy(o => o.Name, StringComparer.Ordinal))
        { previous.Add(start, null); pending.Enqueue(start); }
        while (pending.TryDequeue(out var current))
        {
            if (current.Kind == OwnerKind.Component && current.Type == typeof(TComponent))
            {
                var path = new List<CompositionOwner>();
                for (CompositionOwner? step = current; step is not null; step = previous[step]) path.Add(step);
                path.Reverse();
                return path.AsReadOnly();
            }
            foreach (var dependency in entries[current].Dependencies)
                if (previous.TryAdd(dependency, current)) pending.Enqueue(dependency);
        }
        return Array.Empty<CompositionOwner>();
    }
    /// <summary>Gets the active registrations in a service type and key slot, in collection order.</summary>
    /// <param name="serviceType">The service type.</param><param name="key">The service key; null denotes the unkeyed slot.</param>
    public IEnumerable<ServiceRegistration> Active(Type serviceType, object? key = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return Services.Where(s => s.Active && s.ServiceType == serviceType && Equals(s.Key, key));
    }
    /// <summary>Gets every occurrence the owner registered, including removed ones; filter by Active for current registrations.</summary>
    public IEnumerable<ServiceRegistration> OwnedBy(CompositionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return Services.Where(s => s.Owner == owner);
    }
    /// <summary>Gets the declared reads of a configuration section, compared case-insensitively as configuration keys are.</summary>
    public IEnumerable<ConfigurationRead> Reads(string section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return Configuration.Where(r => string.Equals(r.Section, section, StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>Gets the contributors with a builder contribution to exactly <typeparamref name="TTarget"/>, in canonical order.</summary>
    public IEnumerable<CompositionOwner> ContributorsTo<TTarget>() =>
        Contributions.Where(c => c.TargetType == typeof(TTarget)).Select(c => c.Owner).Distinct();
}

/// <summary>Reports composition errors and the available manifest snapshot.</summary>
public sealed class CompositionException : Exception
{
    internal CompositionException(IEnumerable<CompositionDiagnostic> diagnostics, CompositionManifest? manifest = null, Exception? innerException = null)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Code}: {d.Message}")), innerException)
    { Diagnostics = Array.AsReadOnly(diagnostics.ToArray()); Manifest = manifest; }
    /// <summary>Gets the errors responsible for failure.</summary>
    public IReadOnlyList<CompositionDiagnostic> Diagnostics { get; }
    /// <summary>Gets the available snapshot, or null for definition/selection failures.</summary>
    public CompositionManifest? Manifest { get; }
}
