using System.Reflection;

namespace Corfedo.Troupe;

internal sealed class DefinitionBuilder : ICompositionDefinitionBuilder
{
    internal Type? DefinitionType;
    internal readonly Dictionary<string, RoleDefinition> Roles = new(StringComparer.Ordinal);
    internal readonly List<ProfileIdentity> Profiles = [];
    internal readonly List<ICompositionRule> Rules = [];
    // Each entry adds one definition-level requirement to the definition's root contributor.
    internal readonly List<Action<ContributorBuilder>> Requirements = [];
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

    public ICompositionDefinitionBuilder Requires<T>() where T : IComponent, new()
    {
        EnsureOpen();
        Requirements.Add(root => root.Requires<T>());
        return this;
    }

    public ICompositionDefinitionBuilder Role<T>() where T : IRole, new()
    {
        EnsureOpen();
        Register<T>(reuse: false);
        return this;
    }

    // A role type registers once under its attribute name; profiles naming it again reuse that registration.
    private string Register<T>(bool reuse) where T : IRole, new()
    {
        var name = typeof(T).GetCustomAttribute<RoleAttribute>()?.Name;
        if (string.IsNullOrWhiteSpace(name) || name.Contains(',') || name != name.Trim())
            throw Errors.Fail("TRP010", $"Role {typeof(T).FullName} needs a nonempty, trimmed name without commas.");
        if (reuse && Roles.TryGetValue(name, out var existing) && existing.Identity.Type == typeof(T)) return name;
        if (Roles.ContainsKey(name) || Roles.Values.Any(r => r.Identity.Type == typeof(T)))
            throw Errors.Fail("TRP010", $"Duplicate role identity {name} ({typeof(T).FullName}).");
        Roles.Add(name, new(new(typeof(T), name), b => new T().Compose(b)));
        return name;
    }

    public ICompositionDefinitionBuilder Profile(string name, Action<IProfileBuilder> roles)
    {
        EnsureOpen();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(roles);
        if (Profiles.Any(p => p.Name == name)) throw Errors.Fail("TRP011", $"Duplicate profile name {name}.");
        var profile = new ProfileBuilder(this);
        roles(profile);
        if (profile.Names.Count == 0) throw Errors.Fail("TRP011", $"Profile {name} cannot be empty.");
        Profiles.Add(new(name, Array.AsReadOnly(profile.Names.Order(StringComparer.Ordinal).ToArray())));
        return this;
    }

    private sealed class ProfileBuilder(DefinitionBuilder definition) : IProfileBuilder
    {
        internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
        public IProfileBuilder Role<T>() where T : IRole, new()
        {
            definition.EnsureOpen();
            Names.Add(definition.Register<T>(reuse: true));
            return this;
        }
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
        return definition;
    }
}

internal sealed record RoleDefinition(RoleIdentity Identity, Action<ContributorBuilder> Declare);
