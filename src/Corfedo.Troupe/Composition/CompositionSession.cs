using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe;

internal sealed class CompositionSession(IHostApplicationBuilder builder, DefinitionBuilder definition,
    RoleIdentity[] roles, ContributorBuilder[] nodes) : IComposition
{
    internal readonly IHostApplicationBuilder Builder = builder;
    internal readonly DefinitionBuilder Definition = definition;
    internal readonly ContributorBuilder[] Nodes = nodes;
    internal readonly ServiceLedger Ledger = new(builder.Services);
    internal readonly List<PendingContribution> Contributions = [];
    internal readonly List<StartupCheck> Checks = [];
    internal readonly Dictionary<Type, Action<IServiceProvider>> ResolveOptions = [];
    internal readonly HashSet<Type> Applied = [];
    internal readonly HashSet<Type> Multiple = [];
    internal readonly HashSet<(Type Type, object? Key)> Exclusive = [];
    internal readonly List<ConfigurationRead> Configuration = [];
    internal readonly List<ICompositionRule> InternalRules = [];
    internal readonly List<CompositionDiagnostic> AdditionalDiagnostics = [];
    internal readonly Dictionary<Type, object> Extensions = [];
    internal bool Finalized;
    public IReadOnlyList<RoleIdentity> Roles { get; } = Array.AsReadOnly(roles);
    public bool Contains<TRole>() where TRole : IRole => Roles.Any(r => r.Type == typeof(TRole));
    public CompositionManifest Manifest => Snapshot();
    internal void Track(CompositionOwner owner, Action<IServiceCollection> callback, bool privileged = false) => Ledger.Configure(owner, callback, privileged);
    internal CompositionManifest Snapshot(bool evaluateRules = true)
    {
        var manifest = new CompositionManifest(Roles,
            Array.AsReadOnly(Nodes.Select(n => new ContributorEntry(n.Owner,
                Array.AsReadOnly(n.Requirements.Keys.Select(t => Nodes.Single(x => x.Owner.Type == t).Owner).OrderBy(o => o.Name, StringComparer.Ordinal).ToArray()))).ToArray()),
            Array.AsReadOnly(Ledger.Registrations()), Array.AsReadOnly(Ledger.Mutations()),
            Array.AsReadOnly(Configuration.ToArray()),
            Array.AsReadOnly(Contributions.Select(c => new ContributionEntry(c.Owner, c.Target, c.Applied)).ToArray()),
            Array.AsReadOnly(AdditionalDiagnostics.ToArray()));
        if (!evaluateRules) return manifest;
        var diagnostics = CoreRules.Evaluate(this, manifest).Concat(InternalRules.SelectMany(r => r.Evaluate(manifest))).Concat(AdditionalDiagnostics).ToList();
        manifest = new(manifest.Roles, manifest.Contributors, manifest.Services, manifest.ServiceMutations,
            manifest.Configuration, manifest.Contributions, Array.AsReadOnly(diagnostics.ToArray()));
        foreach (var rule in Definition.Rules) diagnostics.AddRange(rule.Evaluate(manifest));
        return new(manifest.Roles, manifest.Contributors, manifest.Services, manifest.ServiceMutations,
            manifest.Configuration, manifest.Contributions, Array.AsReadOnly(diagnostics.Distinct().ToArray()));
    }
    internal CompositionManifest Validate(bool finalize = false)
    {
        Ledger.Synchronize();
        if (finalize)
        {
            foreach (var contribution in Contributions.Where(c => !c.Applied))
            {
                var diagnostic = new CompositionDiagnostic("TRP008", DiagnosticSeverity.Error,
                    $"Unconsumed contribution to {contribution.Target.FullName} from {contribution.Owner.Name}.", Array.AsReadOnly(new[] { contribution.Owner }));
                if (!AdditionalDiagnostics.Contains(diagnostic)) AdditionalDiagnostics.Add(diagnostic);
            }
        }
        var manifest = Snapshot();
        if (manifest.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new CompositionException(manifest.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error), manifest);
        if (finalize) Finalized = true;
        return manifest;
    }
    internal static CompositionSession Get(IHostApplicationBuilder builder) =>
        builder.Properties.TryGetValue(typeof(CompositionSession), out var value) && value is CompositionSession session
            ? session : throw new InvalidOperationException("Compose a definition first.");
}
internal sealed class PendingContribution(CompositionOwner owner, Type target, Action<object> callback)
{
    internal readonly CompositionOwner Owner = owner;
    internal readonly Type Target = target;
    internal readonly Action<object> Callback = callback;
    internal bool Applied;
}
