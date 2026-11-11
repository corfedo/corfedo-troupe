using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe.AspNetCore;

/// <summary>A contributor's claim on the controllers in a namespace and its child namespaces of one assembly.</summary>
/// <param name="Owner">The claiming contributor.</param><param name="Assembly">The controllers' assembly.</param>
/// <param name="Namespace">The claimed namespace.</param>
public sealed record ControllerClaim(CompositionOwner Owner, Assembly Assembly, string Namespace);

/// <summary>Assigns controllers to the contributors that claim them. With <see cref="ControllerClaims"/>, every process that registers
/// MVC serves only the controllers that a composed contributor claims, and their endpoints carry the claiming owner.</summary>
public static class ComposedControllers
{
    /// <summary>Serves only claimed controllers in every composition of this definition, including one without any claim.</summary>
    /// <remarks>Required for <see cref="Controllers{TMarker}"/>. Claims of different contributors must not overlap (TRP109).</remarks>
    public static ICompositionDefinitionBuilder ControllerClaims(this ICompositionDefinitionBuilder definition)
    {
        if (definition is not DefinitionBuilder builder) throw new ArgumentException("ASP.NET declarations require Troupe's definition builder.", nameof(definition));
        builder.Declare((session, owner) =>
        {
            if (session.TryIntegration<WebComposition>() is null) session.AttachIntegration(new WebComposition(session, []));
            var claims = session.Integration<WebComposition>().Controllers;
            if (claims.Declared) return;
            claims.Declared = true;
            session.ConfigureServices(owner, services => services.Configure<MvcOptions>(options => options.Conventions.Add(new ClaimedControllerOwners(claims))));
        });
        return definition;
    }

    /// <summary>Claims the controllers in the namespace of <typeparamref name="TMarker"/> and its child namespaces, in its assembly.</summary>
    /// <remarks>Requires <see cref="ControllerClaims"/> on the definition and MVC (AddControllers, AddControllersWithViews or AddRazorPages)
    /// in the composed host; TRP107 otherwise.</remarks>
    public static void Controllers<TMarker>(this IContributorBuilder contributor)
    {
        if (contributor is not ContributorBuilder builder) throw new ArgumentException("ASP.NET declarations require Troupe's contributor builder.", nameof(contributor));
        var marker = typeof(TMarker);
        builder.UseIntegration(session => new WebComposition(session, []));
        builder.Record((session, owner) => session.Integration<WebComposition>().Controllers.Add(new(owner, marker.Assembly, marker.Namespace ?? "")));
    }

    // Pairs of claims of different owners in one assembly whose namespaces are equal or nested.
    internal static IEnumerable<(ControllerClaim First, ControllerClaim Second)> Overlaps(IEnumerable<ControllerClaim> claims)
    {
        var distinct = claims.Distinct().OrderBy(c => c.Owner.Name, StringComparer.Ordinal).ThenBy(c => c.Namespace, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < distinct.Length; i++)
            for (var j = i + 1; j < distinct.Length; j++)
                if (distinct[i].Owner != distinct[j].Owner && distinct[i].Assembly == distinct[j].Assembly &&
                    (Within(distinct[i].Namespace, distinct[j].Namespace) || Within(distinct[j].Namespace, distinct[i].Namespace)))
                    yield return (distinct[i], distinct[j]);
    }
    internal static CompositionDiagnostic Overlap(ControllerClaim first, ControllerClaim second) => new("TRP109", DiagnosticSeverity.Error,
        $"{first.Owner.Name} claims controllers in {Describe(first.Namespace)} and {second.Owner.Name} in {Describe(second.Namespace)} of " +
        $"{first.Assembly.GetName().Name}; claims of different contributors must not overlap.", Array.AsReadOnly(new[] { first.Owner, second.Owner }));
    private static string Describe(string ns) => ns.Length == 0 ? "the global namespace" : ns;
    private static bool Within(string ns, string claim) => claim.Length == 0 || ns == claim || ns.StartsWith(claim + ".", StringComparison.Ordinal);

    // The claims of one composition; with the definition declaration, a feature provider removes the controllers no claim covers.
    internal sealed class ClaimSet
    {
        private readonly List<ControllerClaim> claims = [];
        internal bool Declared;
        internal IReadOnlyList<ControllerClaim> Claims => claims;
        internal void Add(ControllerClaim claim) => claims.Add(claim);
        // Claims of different owners do not overlap, so at most one owner matches.
        internal CompositionOwner? Owner(TypeInfo controller) =>
            claims.FirstOrDefault(c => c.Assembly == controller.Assembly && Within(controller.Namespace ?? "", c.Namespace))?.Owner;

        // Runs after MVC's ControllerFeatureProvider, which AddMvcCore adds first.
        internal void Install(ApplicationPartManager manager)
        {
            if (!manager.FeatureProviders.OfType<ClaimedControllers>().Any(p => p.Claims == this)) manager.FeatureProviders.Add(new ClaimedControllers(this));
        }

        // Discovered controllers come from MVC's feature providers without the claim filter, independent of mapping. A controller is
        // served when an endpoint maps one of its actions. A claimed controller without actions has nothing to map; the actions of
        // an unclaimed controller are unknown here, so it counts as unserved.
        internal (Type[] Served, Type[] Unserved) Inventory(IServiceProvider services, IEnumerable<EndpointEntry> endpoints)
        {
            if (!Declared || services.GetService<ApplicationPartManager>() is not { } manager) return ([], []);
            var discovered = new ControllerFeature();
            foreach (var provider in manager.FeatureProviders.OfType<IApplicationFeatureProvider<ControllerFeature>>().Where(p => p is not ClaimedControllers))
                provider.PopulateFeature(manager.ApplicationParts, discovered);
            var mapped = endpoints.Select(e => e.Endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo).OfType<TypeInfo>().ToHashSet();
            var claimedUnmapped = discovered.Controllers.Where(c => !mapped.Contains(c) && Owner(c) is not null).ToArray();
            // MVC builds the action descriptors only when a claimed controller has no endpoint and its actions decide the result.
            var withActions = claimedUnmapped.Length == 0 ? [] : services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
                .OfType<ControllerActionDescriptor>().Select(a => a.ControllerTypeInfo).ToHashSet();
            static Type[] Sorted(IEnumerable<TypeInfo> types) => types.Distinct().Select(t => t.AsType()).OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
            return (Sorted(discovered.Controllers.Where(mapped.Contains)),
                Sorted(discovered.Controllers.Where(c => !mapped.Contains(c) && (Owner(c) is null || withActions.Contains(c)))));
        }
    }

    private sealed class ClaimedControllers(ClaimSet claims) : IApplicationFeatureProvider<ControllerFeature>
    {
        internal ClaimSet Claims => claims;
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in feature.Controllers.ToArray())
                if (claims.Owner(controller) is null) feature.Controllers.Remove(controller);
        }
    }
    private sealed class ClaimedControllerOwners(ClaimSet claims) : IControllerModelConvention
    {
        public void Apply(ControllerModel controller)
        {
            if (claims.Owner(controller.ControllerType) is not { } owner) return;
            foreach (var selector in controller.Actions.SelectMany(a => a.Selectors)) selector.EndpointMetadata.Add(new EndpointOwner(owner));
        }
    }
}
