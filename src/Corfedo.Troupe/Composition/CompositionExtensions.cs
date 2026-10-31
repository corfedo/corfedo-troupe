using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe;

/// <summary>Composes selected declarations and applies application-owned builder callbacks.</summary>
public static class CompositionExtensions
{
    /// <summary>
    /// Reads the selection (Profile configuration when the definition declares profiles, otherwise Roles), resolves the selected graph, and
    /// executes recorded service callbacks.
    /// </summary>
    public static CompositionManifest Compose<T>(this IHostApplicationBuilder builder) where T : ICompositionDefinition, new() =>
        Compose(builder, () =>
        {
            var definition = DefinitionBuilder.Create<T>();
            var (profile, roles) = Graph.Select(definition, builder.Configuration["Profile"], builder.Configuration["Roles"]);
            return (definition, profile, roles.Select(n => definition.Roles[n].Identity).ToArray(), Graph.ResolveRoles(definition, roles));
        });

    // Shared by role selection and test roots: the builder composes once, and the resolved closure executes in canonical order.
    internal static CompositionManifest Compose(IHostApplicationBuilder builder,
        Func<(DefinitionBuilder Definition, ProfileIdentity? Profile, RoleIdentity[] Roles, ContributorBuilder[] Nodes)> resolve)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (CompositionSession.TryGet(builder) is { } existing)
            throw existing.Fail(DiagnosticCodes.InvalidPhase, "A builder can compose only once.", ServiceLedger.Application);
        var (definition, profile, roles, nodes) = resolve();
        try
        {
            var session = new CompositionSession(builder, definition, profile, roles, nodes);
            builder.Properties.Add(typeof(CompositionSession), session);
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
    /// <summary>Gets the serving profiles the definition declares, in declaration order, without composing.</summary>
    public static IReadOnlyList<ProfileIdentity> DeclaredProfiles<T>() where T : ICompositionDefinition, new() =>
        Array.AsReadOnly(DefinitionBuilder.Create<T>().Profiles.ToArray());
    /// <summary>Applies callbacks once for the exact target type in canonical order. Library descriptors are application-owned.</summary>
    public static void ApplyContributions<T>(this IHostApplicationBuilder builder, T target)
    {
        ArgumentNullException.ThrowIfNull(target);
        CompositionSession.Get(builder).ApplyContributions(target);
    }
    /// <summary>Captures adapter registrations, rejects unconsumed contributions, and validates before Build.</summary>
    /// <remarks>Required after adapters and before Build. Pipeline application and startup revalidate the finalized composition and fail with TRP018 when this call was skipped. The application keeps ValidateOnBuild and ValidateScopes enabled.</remarks>
    public static CompositionManifest FinalizeComposition(this IHostApplicationBuilder builder) => CompositionSession.Get(builder).FinalizeComposition();
}
