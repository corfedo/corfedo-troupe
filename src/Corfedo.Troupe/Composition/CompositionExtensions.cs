using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe;

/// <summary>Composes selected declarations and applies application-owned builder callbacks.</summary>
public static class CompositionExtensions
{
    /// <summary>Reads Roles configuration, resolves the selected graph, and executes recorded service callbacks.</summary>
    public static CompositionManifest Compose<T>(this IHostApplicationBuilder builder) where T : ICompositionDefinition, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Properties.ContainsKey(typeof(CompositionSession))) throw new InvalidOperationException("A builder can compose only once.");
        var definition = DefinitionBuilder.Create<T>();
        var (roles, nodes) = Graph.Resolve(definition, builder.Configuration["Roles"]);
        var session = new CompositionSession(builder, definition, roles, nodes);
        builder.Properties.Add(typeof(CompositionSession), session);
        session.Ledger.Synchronize();
        try
        {
            foreach (var node in nodes)
                foreach (var action in node.Actions) action(session, node.Owner);
            builder.Services.AddSingleton(session);
            builder.Services.AddSingleton<IComposition>(session);
            builder.Services.AddSingleton<IHostedService, CompositionLifecycle>();
            return session.Validate();
        }
        catch (CompositionException exception) when (exception.Manifest is null)
        { session.AdditionalDiagnostics.AddRange(exception.Diagnostics); throw new CompositionException(exception.Diagnostics, session.Snapshot()); }
    }
    /// <summary>Applies callbacks once for the exact target type in canonical order. Library descriptors are application-owned.</summary>
    public static void ApplyContributions<T>(this IHostApplicationBuilder builder, T target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var session = CompositionSession.Get(builder);
        if (session.Finalized) throw new InvalidOperationException("Composition is finalized.");
        if (!session.Applied.Add(typeof(T))) throw Errors.Fail("TRP008", $"Contributions to {typeof(T)} have already been applied.");
        foreach (var contribution in session.Contributions.Where(c => c.Target == typeof(T)))
        { contribution.Callback(target); contribution.Applied = true; }
        session.Validate();
    }
    /// <summary>Captures adapter registrations, rejects unconsumed contributions, and validates before Build.</summary>
    /// <remarks>Call after application adapters and before Build. The application keeps ValidateOnBuild and ValidateScopes enabled.</remarks>
    public static CompositionManifest FinalizeComposition(this IHostApplicationBuilder builder) => CompositionSession.Get(builder).Validate(finalize: true);
}
