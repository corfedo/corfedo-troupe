using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe.AspNetCore;

/// <summary>Records application-ordered middleware, owner groups and named listener requirements.</summary>
public static class AspNetCompositionExtensions
{
    /// <summary>Declares the stage order. The application explicitly places UseRouting before endpoint-aware middleware.</summary>
    public static void Stages(this ICompositionDefinitionBuilder definition, params string[] stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        if (stages.Any(string.IsNullOrWhiteSpace) || stages.Distinct(StringComparer.Ordinal).Count() != stages.Length)
            throw Errors.Fail("TRP100", "Stage names must be nonempty and unique.");
        var builder = (DefinitionBuilder)definition;
        if (!builder.Extensions.TryAdd(typeof(WebComposition), (string[])stages.Clone()))
            throw Errors.Fail("TRP100", "Declare the stage sequence once.");
    }
    /// <summary>Records middleware for an application-declared stage, ordered by contributor within that stage.</summary>
    /// <remarks>Root authentication and authorization belong to explicit application components. Conditional authentication suppression belongs to the application adapter.</remarks>
    public static void Use(this IContributorBuilder contributor, string stage, Action<IApplicationBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage); ArgumentNullException.ThrowIfNull(configure);
        ((ContributorBuilder)contributor).Actions.Add((s, o) => WebComposition.Get(s).Middleware.Add((o, stage, configure)));
    }
    /// <summary>Maps endpoints after all stages in an empty-prefix group carrying owner metadata.</summary>
    public static void Endpoints(this IContributorBuilder contributor, Action<IEndpointRouteBuilder> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        ((ContributorBuilder)contributor).Actions.Add((s, o) => WebComposition.Get(s).Mappings.Add((o, map)));
    }
    /// <summary>Requires Kestrel:Endpoints:name. This declares topology; it does not configure TLS or bind an address.</summary>
    public static void KestrelEndpoint(this IContributorBuilder contributor, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains(':')) throw new ArgumentException("Use one endpoint name, not a configuration path.", nameof(name));
        ((ContributorBuilder)contributor).Actions.Add((s, o) =>
        {
            var web = WebComposition.Get(s);
            if (!web.Requested.TryGetValue(name, out var owners)) web.Requested.Add(name, owners = []);
            if (!owners.Contains(o)) owners.Add(o);
        });
    }
    /// <summary>Finalizes composition, applies stages once, maps owner groups, and validates resulting endpoints.</summary>
    public static WebApplication UseComposition(this WebApplication application)
    {
        var session = application.Services.GetRequiredService<CompositionSession>();
        var web = WebComposition.Get(session);
        if (web.Applied) throw new InvalidOperationException("The composition pipeline has already been applied.");
        web.Applied = true;
        session.Validate(finalize: true);
        foreach (var stage in web.Stages)
            foreach (var contribution in web.Middleware.Where(m => m.Stage == stage)) contribution.Configure(application);
        if (web.Mappings.Count > 0 && !((IApplicationBuilder)application).Properties.ContainsKey("__EndpointRouteBuilder"))
            throw Errors.Fail("TRP100", "Endpoint composition requires an explicit root UseRouting callback in an application-declared stage.");
        foreach (var (owner, map) in web.Mappings)
            map(application.MapGroup("").WithMetadata(new EndpointOwner(owner)));
        web.Endpoints.AddRange(((IEndpointRouteBuilder)application).DataSources.SelectMany(d => d.Endpoints)
            .Select(e => new EndpointEntry(e, e.Metadata.GetMetadata<EndpointOwner>()?.Owner ?? ServiceLedger.Application)));
        session.Validate();
        return application;
    }
    /// <summary>Gets a read-only ASP.NET manifest snapshot from the runtime composition.</summary>
    public static AspNetCompositionManifest AspNetCore(this IComposition composition)
    {
        if (composition is not CompositionSession session) throw new ArgumentException("The composition was not created by Troupe.", nameof(composition));
        return WebComposition.Get(session).Snapshot();
    }
}
