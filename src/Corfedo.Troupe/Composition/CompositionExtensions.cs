using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe;

/// <summary>Composes selected declarations and applies application-owned builder callbacks.</summary>
public static class CompositionExtensions
{
    /// <summary>
    /// Reads the selection (Profile configuration when the definition declares profiles, otherwise Roles), resolves the selected graph,
    /// executes recorded service callbacks, and installs the service provider factory that finalizes the composition in Build.
    /// </summary>
    /// <remarks>
    /// Build captures the registrations adapters made after this call, rejects contributions no adapter applied (TRP008), and builds the
    /// default provider with ValidateOnBuild and ValidateScopes. Another ConfigureContainer, Host.UseServiceProviderFactory or
    /// Host.UseDefaultServiceProvider call replaces that factory, and pipeline application and startup then fail with TRP018.
    /// When the TROUPE_DESCRIBE environment variable names a file, this call composes the same selection, writes its description to
    /// that file and ends the process with exit code 0; composition errors are part of the description.
    /// </remarks>
    public static CompositionManifest AddTroupe<T>(this IHostApplicationBuilder builder) where T : ICompositionDefinition, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (CompositionDescription.Requested() is { } description) CompositionDescription.Describe<T>(builder, description);
        return Compose(builder, () => Resolve(DefinitionBuilder.Create<T>(), builder.Configuration));
    }

    internal static (DefinitionBuilder Definition, ProfileIdentity? Profile, RoleIdentity[] Roles, ContributorBuilder[] Nodes) Resolve(
        DefinitionBuilder definition, IConfiguration configuration)
    {
        var (profile, roles) = Graph.Select(definition, configuration["Profile"], configuration["Roles"]);
        return (definition, profile, roles.Select(n => definition.Roles[n].Identity).ToArray(), Graph.ResolveRoles(definition, roles));
    }

    // Shared by role selection and test roots: the builder composes once, and the resolved closure executes in canonical order.
    internal static CompositionManifest Compose(IHostApplicationBuilder builder,
        Func<(DefinitionBuilder Definition, ProfileIdentity? Profile, RoleIdentity[] Roles, ContributorBuilder[] Nodes)> resolve)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (CompositionSession.TryGet(builder) is { } existing)
            throw existing.Fail(DiagnosticCodes.InvalidPhase, "A builder runs AddTroupe or ComposeForTest only once.", ServiceLedger.Application);
        var (definition, profile, roles, nodes) = resolve();
        try
        {
            var session = new CompositionSession(builder, definition, profile, roles, nodes);
            builder.Properties.Add(typeof(CompositionSession), session);
            builder.ConfigureContainer(new CompositionProviderFactory(session));
            HostBuildingObserver.Watch(builder.Properties, session);
            var initialize = definition.IntegrationFactory ?? nodes.Select(n => n.IntegrationFactory).FirstOrDefault(f => f is not null);
            if (initialize is not null) session.AttachIntegration(initialize(session));
            session.ValidateDeclarations();
            foreach (var declaration in definition.Declarations) session.Execute(definition.Owner, declaration);
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
    /// <summary>Applies callbacks once for the exact target type in canonical order.</summary>
    /// <remarks>Registrations a callback adds synchronously to the builder's service collection are owned by its contributor; removing
    /// another owner's registration fails with TRP003. Registrations the adapter makes outside the callbacks are application-owned.</remarks>
    public static void ApplyContributions<T>(this IHostApplicationBuilder builder, T target)
    {
        ArgumentNullException.ThrowIfNull(target);
        CompositionSession.Get(builder).ApplyContributions(target);
    }
}
