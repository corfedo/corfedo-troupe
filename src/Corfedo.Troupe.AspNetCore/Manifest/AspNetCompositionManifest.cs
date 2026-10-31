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
        IReadOnlyList<KestrelEndpointEntry> listeners)
    { Stages = stages; Endpoints = endpoints; KestrelEndpoints = listeners; }
    /// <summary>Gets the application-declared stage order.</summary>
    public IReadOnlyList<string> Stages { get; }
    /// <summary>Gets endpoints inspected after mapping.</summary>
    public IReadOnlyList<EndpointEntry> Endpoints { get; }
    /// <summary>Gets named requirements and tracked callback ownership.</summary>
    public IReadOnlyList<KestrelEndpointEntry> KestrelEndpoints { get; }
}
