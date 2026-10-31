namespace Corfedo.Troupe;

internal static class Graph
{
    internal static (RoleIdentity[] Roles, ContributorBuilder[] Nodes) Resolve(DefinitionBuilder definition, string? selection)
    {
        var names = (selection ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) throw Errors.Fail("TRP012", "Select at least one role using Roles configuration.");
        var unknown = names.Where(n => !definition.Roles.ContainsKey(n)).ToArray();
        if (unknown.Length > 0) throw Errors.Fail("TRP012", $"Unknown roles: {string.Join(", ", unknown)}.");
        if (definition.Profiles.Count > 0 && !definition.Profiles.Any(p => names.SequenceEqual(p.Roles)))
            throw Errors.Fail("TRP013", $"No declared profile for {string.Join(",", names)}.");

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
        return (names.Select(n => definition.Roles[n].Identity).ToArray(), ordered.ToArray());
    }
}
