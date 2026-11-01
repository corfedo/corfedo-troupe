using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        var recorded = (string[])stages.Clone();
        Definition(definition).SetIntegration(session => new WebComposition(session, recorded));
    }
    /// <summary>Records middleware for an application-declared stage, ordered by contributor within that stage.</summary>
    /// <remarks>Root authentication and authorization belong to explicit application components. Conditional authentication suppression belongs to the application adapter.</remarks>
    public static void Use(this IContributorBuilder contributor, string stage, Action<IApplicationBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage); ArgumentNullException.ThrowIfNull(configure);
        Record(contributor, (s, o) => s.Integration<WebComposition>().AddMiddleware(o, stage, configure));
    }
    /// <summary>Maps endpoints after all stages in an empty-prefix group carrying owner metadata.</summary>
    public static void Endpoints(this IContributorBuilder contributor, Action<IEndpointRouteBuilder> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        Record(contributor, (s, o) => s.Integration<WebComposition>().AddEndpoints(o, map));
    }
    /// <summary>Requires Kestrel:Endpoints:name. This declares topology; it does not configure TLS or bind an address.</summary>
    /// <remarks>Troupe configures Kestrel's named endpoint loader from the Kestrel section without topology reload.
    /// Code listeners beside named endpoints must use <c>UseHttps(HttpsConnectionAdapterOptions)</c>,
    /// <c>UseHttps(TlsHandshakeCallbackOptions)</c> or <c>UseHttps(ServerOptionsSelectionCallback, state)</c>. The other UseHttps
    /// overloads, including <c>UseHttps(X509Certificate2)</c> and <c>UseHttps(Action&lt;HttpsConnectionAdapterOptions&gt;)</c>,
    /// load the endpoint configuration immediately, and an endpoint callback attached afterwards would never run; that fails
    /// with TRP106.</remarks>
    public static void KestrelEndpoint(this IContributorBuilder contributor, string name)
    {
        EndpointName(name);
        Record(contributor, (s, o) => s.Integration<WebComposition>().Listeners.Require(s, name, o));
    }
    /// <summary>Requires Kestrel:Endpoints:name and owns its single callback, for example its TLS and client certificate policy.</summary>
    /// <remarks>Runs only when this contributor is composed. A second owner, including the application, is rejected. Resolve DI through ListenOptions.ApplicationServices.
    /// Code listeners must not load the endpoint configuration before the callback is attached; see the overload without a callback.</remarks>
    public static void KestrelEndpoint(this IContributorBuilder contributor, string name, Action<EndpointConfiguration> configure)
    {
        EndpointName(name); ArgumentNullException.ThrowIfNull(configure);
        Record(contributor, (s, o) => s.Integration<WebComposition>().Listeners.RegisterOwnedCallback(s, o, name, configure));
    }
    private static void EndpointName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains(':')) throw new ArgumentException("Use one endpoint name, not a configuration path.", nameof(name));
    }
    /// <summary>Finalizes composition, applies stages once, maps owner groups, and validates resulting endpoints.</summary>
    /// <remarks>Captures the endpoint inventory at this call. Map application endpoints before this call or declare them with Endpoints. Recorded pipelines must be applied before startup.</remarks>
    public static WebApplication UseComposition(this WebApplication application)
    {
        var session = application.Services.GetRequiredService<CompositionSession>();
        session.BeginPipeline();
        session.TryIntegration<WebComposition>()?.Apply(application);
        session.CompletePipeline();
        return application;
    }
    /// <summary>Gets a read-only ASP.NET manifest snapshot from the runtime composition.</summary>
    public static AspNetCompositionManifest AspNetCore(this IComposition composition)
    {
        if (composition is not RuntimeComposition runtime) throw new ArgumentException("The composition was not created by Troupe.", nameof(composition));
        return runtime.IntegrationSnapshot<AspNetCompositionManifest>() ?? new(Array.Empty<string>(), Array.Empty<EndpointEntry>(), Array.Empty<KestrelEndpointEntry>(), complete: true);
    }
    /// <summary>Gets the ASP.NET snapshot of the builder's composition, including after a failed AddTroupe.</summary>
    /// <remarks>Reports stages and named Kestrel requirements before Build, for example to derive listeners when configuration is absent;
    /// check <see cref="AspNetCompositionManifest.Complete"/> first. Null when definition or graph resolution failed, so no composition
    /// session exists, or when the composition has no web declarations.</remarks>
    public static AspNetCompositionManifest? AspNetComposition(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return CompositionSession.TryGet(builder)?.IntegrationSnapshot<AspNetCompositionManifest>();
    }
    private static DefinitionBuilder Definition(ICompositionDefinitionBuilder builder) => builder as DefinitionBuilder ??
        throw new ArgumentException("ASP.NET declarations require Troupe's definition builder.", nameof(builder));
    private static void Record(IContributorBuilder contributor, Action<CompositionSession, CompositionOwner> action)
    {
        if (contributor is not ContributorBuilder builder)
            throw new ArgumentException("ASP.NET declarations require Troupe's contributor builder.", nameof(contributor));
        builder.UseIntegration(session => new WebComposition(session, []));
        builder.Record(action);
    }
}
