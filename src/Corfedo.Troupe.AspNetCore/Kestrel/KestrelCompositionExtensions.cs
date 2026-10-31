using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel;

namespace Corfedo.Troupe.AspNetCore;

/// <summary>Installs the application-owned customization of a required Kestrel named endpoint.</summary>
public static class KestrelCompositionExtensions
{
    /// <summary>Registers one callback for a requested name, rejecting a second before Kestrel can replace it.</summary>
    /// <remarks>Uses the existing ConfigurationLoader reading the Kestrel section; the application disables topology reload on that loader. Raw ConfigureKestrel and Endpoint calls remain application-owned and untracked. Resolve DI through ListenOptions.ApplicationServices.</remarks>
    public static void ConfigureCompositionEndpoint(this WebApplicationBuilder builder, string name, Action<EndpointConfiguration> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(configure);
        var session = CompositionSession.Get(builder);
        session.Integration<WebComposition>().Listeners.RegisterCallback(session, name);
        builder.WebHost.ConfigureKestrel(options =>
        {
            var loader = options.ConfigurationLoader ?? throw new InvalidOperationException("The application must configure Kestrel's named endpoint loader.");
            loader.Endpoint(name, configure);
        });
        session.Validate();
    }
}
