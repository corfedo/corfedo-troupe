using Microsoft.AspNetCore.Http;

namespace Corfedo.Troupe.AspNetCore;

/// <summary>Tags an endpoint with its declaring role, component or application owner.</summary>
/// <param name="Owner">The declaring contributor.</param>
public sealed record EndpointOwner(CompositionOwner Owner);
/// <summary>Describes an inspected endpoint with its ASP.NET metadata.</summary>
/// <param name="Endpoint">The resulting endpoint.</param><param name="Owner">The declaring owner.</param>
public sealed record EndpointEntry(Endpoint Endpoint, CompositionOwner Owner);
/// <summary>Describes a requested named listener and its tracked application callback.</summary>
/// <param name="Name">The Kestrel endpoint name.</param><param name="Owners">The requiring contributors.</param>
/// <param name="CallbackOwner">The application callback owner, or null when no customization is installed.</param>
public sealed record KestrelEndpointEntry(string Name, IReadOnlyList<CompositionOwner> Owners, CompositionOwner? CallbackOwner);
/// <summary>Provides an immutable ASP.NET view without placing ASP.NET types in the core manifest.</summary>
public sealed class AspNetCompositionManifest
{
    internal AspNetCompositionManifest(IReadOnlyList<string> stages, IReadOnlyList<EndpointEntry> endpoints,
        IReadOnlyList<KestrelEndpointEntry> listeners, bool complete, IReadOnlyList<Type>? servedControllers = null,
        IReadOnlyList<Type>? unservedControllers = null, IReadOnlyList<ControllerClaim>? controllerClaims = null)
    {
        Stages = stages; Endpoints = endpoints; KestrelEndpoints = listeners; Complete = complete;
        ServedControllers = servedControllers ?? []; UnservedControllers = unservedControllers ?? []; ControllerClaims = controllerClaims ?? [];
    }
    /// <summary>Gets the application-declared stage order.</summary>
    public IReadOnlyList<string> Stages { get; }
    /// <summary>Gets endpoints inspected after mapping.</summary>
    public IReadOnlyList<EndpointEntry> Endpoints { get; }
    /// <summary>Gets named requirements and tracked callback ownership.</summary>
    public IReadOnlyList<KestrelEndpointEntry> KestrelEndpoints { get; }
    /// <summary>Gets the controllers with an action endpoint in this process; empty unless the definition declares controller claims.</summary>
    public IReadOnlyList<Type> ServedControllers { get; }
    /// <summary>Gets the controllers MVC discovers that have no action endpoint in this process: unclaimed controllers, and claimed
    /// controllers with actions that nothing maps. Empty unless the definition declares controller claims.</summary>
    public IReadOnlyList<Type> UnservedControllers { get; }
    /// <summary>Gets the controller claims of the composed contributors.</summary>
    public IReadOnlyList<ControllerClaim> ControllerClaims { get; }
    /// <summary>Gets whether every composed contributor's declarations ran.</summary>
    /// <remarks>False when AddTroupe failed before then, for example with TRP007 or a throwing callback: later contributors' stages
    /// and listener requirements are missing. Check it before deriving listeners from a failed composition.</remarks>
    public bool Complete { get; }
}
