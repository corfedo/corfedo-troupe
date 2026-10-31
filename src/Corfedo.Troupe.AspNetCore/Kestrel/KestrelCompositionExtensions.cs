using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel;

namespace Corfedo.Troupe.AspNetCore;

/// <summary>Installs the application-owned customization of a required Kestrel named endpoint.</summary>
public static class KestrelCompositionExtensions
{
    /// <summary>Registers one callback for a requested name, rejecting a second owner, including a contributor, before Kestrel can replace it.</summary>
    /// <remarks>Applies to Troupe's named endpoint loader, which reads the Kestrel section without topology reload; the application does not call KestrelServerOptions.Configure. Raw ConfigureKestrel and Endpoint calls remain application-owned and untracked. Resolve DI through ListenOptions.ApplicationServices.</remarks>
    public static void ConfigureCompositionEndpoint(this WebApplicationBuilder builder, string name, Action<EndpointConfiguration> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(configure);
        var session = CompositionSession.Get(builder);
        var listeners = session.Integration<WebComposition>().Listeners;
        listeners.RegisterCallback(session, name);
        builder.WebHost.ConfigureKestrel(listeners.EndpointCallback(name, configure));
        session.Validate();
    }
}
