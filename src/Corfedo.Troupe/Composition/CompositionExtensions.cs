using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe;

/// <summary>Composes selected declarations and applies application-owned builder callbacks.</summary>
public static class CompositionExtensions
{
    /// <summary>Reads Roles configuration, resolves the selected graph, and executes recorded service callbacks.</summary>
    public static CompositionManifest Compose<T>(this IHostApplicationBuilder builder) where T : ICompositionDefinition, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (CompositionSession.TryGet(builder) is { } existing)
            throw existing.Fail(DiagnosticCodes.InvalidPhase, "A builder can compose only once.", ServiceLedger.Application);
        var definition = DefinitionBuilder.Create<T>();
        var (roles, nodes) = Graph.Resolve(definition, builder.Configuration["Roles"]);
        var session = new CompositionSession(builder, definition, roles, nodes);
        builder.Properties.Add(typeof(CompositionSession), session);
        try
        {
            var initialize = definition.IntegrationFactory ?? nodes.Select(n => n.IntegrationFactory).FirstOrDefault(f => f is not null);
            if (initialize is not null) session.AttachIntegration(initialize(session));
            session.ValidateDeclarations();
            foreach (var node in nodes)
                foreach (var action in node.TakeActions()) session.Execute(node.Owner, action);
            session.RegisterFrameworkServices(builder.Services);
            session.CompleteComposition();
            return session.Validate();
        }
        finally { foreach (var node in nodes) node.Release(); }
    }
    /// <summary>Applies callbacks once for the exact target type in canonical order. Library descriptors are application-owned.</summary>
    public static void ApplyContributions<T>(this IHostApplicationBuilder builder, T target)
    {
        ArgumentNullException.ThrowIfNull(target);
        CompositionSession.Get(builder).ApplyContributions(target);
    }
    /// <summary>Captures adapter registrations, rejects unconsumed contributions, and validates before Build.</summary>
    /// <remarks>Optional early finalization after adapters and before Build. Pipeline application and startup use the same finalization operation. The application keeps ValidateOnBuild and ValidateScopes enabled.</remarks>
    public static CompositionManifest FinalizeComposition(this IHostApplicationBuilder builder) => CompositionSession.Get(builder).FinalizeComposition();
}
