using System.Reflection;

namespace Corfedo.Troupe;

internal sealed class DefinitionBuilder : ICompositionDefinitionBuilder
{
    internal Type? DefinitionType;
    internal readonly Dictionary<string, RoleDefinition> Roles = new(StringComparer.Ordinal);
    internal readonly List<ProfileIdentity> Profiles = [];
    internal readonly List<ICompositionRule> Rules = [];
    private bool closed;
    internal Func<CompositionSession, ICompositionIntegration>? IntegrationFactory { get; private set; }
    internal void SetIntegration(Func<CompositionSession, ICompositionIntegration> factory)
    {
        EnsureOpen();
        if (IntegrationFactory is not null) throw Errors.Fail("TRP100", "Declare the stage sequence once.");
        IntegrationFactory = factory;
    }
    private void EnsureOpen()
    { if (closed) throw Errors.Fail(DiagnosticCodes.ClosedDeclaration, "The composition definition is closed."); }

    public ICompositionDefinitionBuilder Role<T>() where T : IRole, new()
    {
        EnsureOpen();
        var name = typeof(T).GetCustomAttribute<RoleAttribute>()?.Name;
        if (string.IsNullOrWhiteSpace(name) || name.Contains(',') || name != name.Trim())
            throw Errors.Fail("TRP010", $"Role {typeof(T).FullName} needs a nonempty, trimmed name without commas.");
        if (Roles.ContainsKey(name) || Roles.Values.Any(r => r.Identity.Type == typeof(T)))
            throw Errors.Fail("TRP010", $"Duplicate role identity {name} ({typeof(T).FullName}).");
        Roles.Add(name, new(new(typeof(T), name), b => new T().Compose(b)));
        return this;
    }

    public ICompositionDefinitionBuilder Profile(string name, params string[] roles)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(roles);
        if (Profiles.Any(p => p.Name == name)) throw Errors.Fail("TRP011", $"Duplicate profile name {name}.");
        Profiles.Add(new(name, Array.AsReadOnly(roles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())));
        return this;
    }

    public ICompositionDefinitionBuilder Rule(ICompositionRule rule)
    { EnsureOpen(); ArgumentNullException.ThrowIfNull(rule); Rules.Add(rule); return this; }

    internal static DefinitionBuilder Create<T>() where T : ICompositionDefinition, new() => Create(typeof(T), d => new T().Define(d));
    // A null type denotes a definition recorded by test composition rather than an application definition class.
    internal static DefinitionBuilder Create(Type? type, Action<ICompositionDefinitionBuilder> define)
    {
        var definition = new DefinitionBuilder { DefinitionType = type };
        try { define(definition); }
        finally { definition.closed = true; }
        foreach (var profile in definition.Profiles)
            if (profile.Roles.Count == 0 || profile.Roles.Any(r => !definition.Roles.ContainsKey(r)))
                throw Errors.Fail("TRP011", $"Profile {profile.Name} must contain known roles and cannot be empty.");
        return definition;
    }
}

internal sealed record RoleDefinition(RoleIdentity Identity, Action<ContributorBuilder> Declare);
