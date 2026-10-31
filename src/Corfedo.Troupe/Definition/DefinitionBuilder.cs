using System.Reflection;

namespace Corfedo.Troupe;

internal sealed class DefinitionBuilder : ICompositionDefinitionBuilder
{
    internal readonly Dictionary<string, RoleDefinition> Roles = new(StringComparer.Ordinal);
    internal readonly List<ProfileIdentity> Profiles = [];
    internal readonly List<ICompositionRule> Rules = [];
    internal readonly Dictionary<Type, object> Extensions = [];

    public ICompositionDefinitionBuilder Role<T>() where T : IRole, new()
    {
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
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(roles);
        if (Profiles.Any(p => p.Name == name)) throw Errors.Fail("TRP011", $"Duplicate profile name {name}.");
        Profiles.Add(new(name, Array.AsReadOnly(roles.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())));
        return this;
    }

    public ICompositionDefinitionBuilder Rule(ICompositionRule rule)
    { ArgumentNullException.ThrowIfNull(rule); Rules.Add(rule); return this; }

    internal static DefinitionBuilder Create<T>() where T : ICompositionDefinition, new()
    {
        var definition = new DefinitionBuilder();
        new T().Define(definition);
        foreach (var profile in definition.Profiles)
            if (profile.Roles.Count == 0 || profile.Roles.Any(r => !definition.Roles.ContainsKey(r)))
                throw Errors.Fail("TRP011", $"Profile {profile.Name} must contain known roles and cannot be empty.");
        return definition;
    }
}

internal sealed record RoleDefinition(RoleIdentity Identity, Action<ContributorBuilder> Declare);

internal static class Errors
{
    internal static CompositionException Fail(string code, string message, params CompositionOwner[] owners) =>
        new([new(code, DiagnosticSeverity.Error, message, Array.AsReadOnly(owners))]);
}
