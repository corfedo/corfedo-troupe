namespace Corfedo.Troupe;

internal static class Graph
{
    // Resolves the closure of the selected roles; the role definitions declare the roots.
    internal static ContributorBuilder[] ResolveRoles(DefinitionBuilder definition, IEnumerable<string> roleNames) =>
        Resolve(roleNames.Select(name => definition.Roles[name])
            .Select(role => (new CompositionOwner(OwnerKind.Role, role.Identity.Name, role.Identity.Type), role.Declare)));

    // Declares each root and its requirement closure, then orders dependencies before dependents with ordinal ready-node ties.
    // A root without a definition type (a test root) cannot be required, so it follows its whole closure.
    internal static ContributorBuilder[] Resolve(IEnumerable<(CompositionOwner Owner, Action<ContributorBuilder> Declare)> roots)
    {
        var nodes = new Dictionary<Type, ContributorBuilder>();
        var all = new List<ContributorBuilder>();
        var path = new List<ContributorBuilder>();
        var complete = new HashSet<Type>();
        void Visit(CompositionOwner owner, Action<ContributorBuilder> declare)
        {
            var type = owner.Type;
            if (type is not null)
            {
                if (nodes.TryGetValue(type, out var existing) && existing.Owner != owner)
                    throw Errors.Fail("TRP010", $"Contributor type {type.FullName} has conflicting role/component identities.");
                if (complete.Contains(type)) return;
                var cycleAt = path.FindIndex(n => n.Owner.Type == type);
                if (cycleAt >= 0)
                    throw Errors.Fail("TRP014", $"Dependency cycle: {string.Join(" -> ", path.Skip(cycleAt).Select(n => n.Owner.Name).Append(owner.Name))}.");
                if (nodes.ContainsKey(type)) throw Errors.Fail("TRP010", $"Contributor type {type.FullName} has conflicting identities.");
            }
            var node = new ContributorBuilder(owner);
            if (type is not null) nodes.Add(type, node);
            all.Add(node);
            path.Add(node);
            try { declare(node); }
            finally { node.Close(); }
            foreach (var (required, callback) in node.Requirements.OrderBy(x => x.Key.FullName, StringComparer.Ordinal))
                Visit(new(OwnerKind.Component, required.FullName ?? required.Name, required), callback);
            path.RemoveAt(path.Count - 1);
            if (type is not null) complete.Add(type);
        }
        foreach (var (owner, declare) in roots) Visit(owner, declare);
        if (all.Select(n => n.Owner.Name).Distinct(StringComparer.Ordinal).Count() != all.Count)
            throw Errors.Fail("TRP010", "Contributor diagnostic names must be unambiguous.");
        var ordered = new List<ContributorBuilder>();
        var remaining = all.ToHashSet();
        var done = new HashSet<ContributorBuilder>();
        while (remaining.Count > 0)
        {
            var next = remaining.Where(n => n.Requirements.Keys.All(t => done.Contains(nodes[t])))
                .OrderBy(n => n.Owner.Name, StringComparer.Ordinal).First();
            remaining.Remove(next); done.Add(next); ordered.Add(next);
        }
        return ordered.ToArray();
    }

    // Declared profiles are the only selection of a composition that declares them; others select roles.
    internal static (ProfileIdentity? Profile, string[] Roles) Select(DefinitionBuilder definition, string? profileSelection, string? roleSelection)
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

    // Fails when the root requires a component that the named declared profile does not compose.
    internal static void RequireWithinProfile(DefinitionBuilder definition, string profileName, ContributorBuilder root)
    {
        var (profile, roles) = Select(definition, profileName, null);
        var closure = ResolveRoles(definition, roles);
        try
        {
            var composed = closure.Select(n => n.Owner.Type).ToHashSet();
            var outside = root.Requirements.Keys.Where(t => !composed.Contains(t)).Select(t => t.FullName ?? t.Name).Order(StringComparer.Ordinal).ToArray();
            if (outside.Length > 0)
                throw Errors.Fail(DiagnosticCodes.OutsideProfile, $"{root.Owner.Name}: profile {profile!.Name} does not compose {string.Join(", ", outside)}.", root.Owner);
        }
        finally { foreach (var node in closure) node.Release(); }
    }
}
