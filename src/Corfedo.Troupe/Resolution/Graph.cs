namespace Corfedo.Troupe;

internal static class Graph
{
    internal static (ProfileIdentity? Profile, RoleIdentity[] Roles, ContributorBuilder[] Nodes) Resolve(DefinitionBuilder definition,
        string? profileSelection, string? roleSelection)
    {
        var (profile, names) = Select(definition, profileSelection, roleSelection);

        var nodes = new Dictionary<Type, ContributorBuilder>();
        var path = new List<ContributorBuilder>();
        var complete = new HashSet<Type>();
        void Visit(Type type, CompositionOwner owner, Action<ContributorBuilder> declare)
        {
            if (nodes.TryGetValue(type, out var existing) && existing.Owner != owner)
                throw Errors.Fail("TRP010", $"Contributor type {type.FullName} has conflicting role/component identities.");
            if (complete.Contains(type)) return;
            var cycleAt = path.FindIndex(n => n.Owner.Type == type);
            if (cycleAt >= 0)
                throw Errors.Fail("TRP014", $"Dependency cycle: {string.Join(" -> ", path.Skip(cycleAt).Select(n => n.Owner.Name).Append(owner.Name))}.");
            if (nodes.ContainsKey(type)) throw Errors.Fail("TRP010", $"Contributor type {type.FullName} has conflicting identities.");
            var node = new ContributorBuilder(owner);
            nodes.Add(type, node);
            path.Add(node);
            try { declare(node); }
            finally { node.Close(); }
            foreach (var (required, callback) in node.Requirements.OrderBy(x => x.Key.FullName, StringComparer.Ordinal))
                Visit(required, new(OwnerKind.Component, required.FullName ?? required.Name, required), callback);
            path.RemoveAt(path.Count - 1);
            complete.Add(type);
        }
        foreach (var name in names)
        {
            var role = definition.Roles[name];
            Visit(role.Identity.Type, new(OwnerKind.Role, name, role.Identity.Type), role.Declare);
        }
        if (nodes.Values.Select(n => n.Owner.Name).Distinct(StringComparer.Ordinal).Count() != nodes.Count)
            throw Errors.Fail("TRP010", "Contributor diagnostic names must be unambiguous.");
        var ordered = new List<ContributorBuilder>();
        var remaining = nodes.Values.ToHashSet();
        var done = new HashSet<Type>();
        while (remaining.Count > 0)
        {
            var next = remaining.Where(n => n.Requirements.Keys.All(done.Contains))
                .OrderBy(n => n.Owner.Name, StringComparer.Ordinal).First();
            remaining.Remove(next); done.Add(next.Owner.Type!); ordered.Add(next);
        }
        return (profile, names.Select(n => definition.Roles[n].Identity).ToArray(), ordered.ToArray());
    }

    // Declared profiles are the only selection of a composition that declares them; others select roles.
    private static (ProfileIdentity? Profile, string[] Roles) Select(DefinitionBuilder definition, string? profileSelection, string? roleSelection)
    {
        var profileName = string.IsNullOrWhiteSpace(profileSelection) ? null : profileSelection.Trim();
        var roleNames = (roleSelection ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (definition.Profiles.Count > 0)
        {
            var declared = string.Join(", ", definition.Profiles.Select(p => p.Name));
            if (roleNames.Length > 0) throw Errors.Fail("TRP013", $"Select a declared profile using Profile configuration, not Roles. Declared profiles: {declared}.");
            if (profileName is null) throw Errors.Fail("TRP013", $"Select a declared profile using Profile configuration. Declared profiles: {declared}.");
            var profile = definition.Profiles.FirstOrDefault(p => p.Name == profileName)
                ?? throw Errors.Fail("TRP013", $"Unknown profile {profileName}. Declared profiles: {declared}.");
            return (profile, [.. profile.Roles]);
        }
        if (profileName is not null) throw Errors.Fail("TRP013", "Profile selection requires declared profiles; select roles using Roles configuration.");
        if (roleNames.Length == 0) throw Errors.Fail("TRP012", "Select at least one role using Roles configuration.");
        var unknown = roleNames.Where(n => !definition.Roles.ContainsKey(n)).ToArray();
        if (unknown.Length > 0) throw Errors.Fail("TRP012", $"Unknown roles: {string.Join(", ", unknown)}.");
        return (null, roleNames);
    }
}
