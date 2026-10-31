using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel;

namespace Corfedo.Troupe.AspNetCore;

/// <summary>Installs the application-owned customization of a required Kestrel named endpoint.</summary>
public static class KestrelCompositionExtensions
{
    /// <summary>Registers one callback for a requested name, rejecting a second before Kestrel can replace it.</summary>
    /// <remarks>Uses the existing ConfigurationLoader; the application disables topology reload on that loader. Raw ConfigureKestrel and Endpoint calls remain application-owned and untracked. Resolve DI through ListenOptions.ApplicationServices.</remarks>
    public static void ConfigureCompositionEndpoint(this WebApplicationBuilder builder, string name, Action<EndpointConfiguration> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(configure);
        var session = CompositionSession.Get(builder);
        if (session.Finalized) throw new InvalidOperationException("Configure endpoint callbacks before finalization.");
        var web = WebComposition.Get(session);
        if (!web.Requested.ContainsKey(name)) throw Errors.Fail("TRP103", $"Kestrel endpoint {name} is not requested.");
        if (!web.Callbacks.TryAdd(name, ServiceLedger.Application))
        {
            var diagnostic = new CompositionDiagnostic("TRP104", DiagnosticSeverity.Error,
                $"Kestrel endpoint {name} already has an application callback owner; a second callback would replace its policy.",
                Array.AsReadOnly(new[] { ServiceLedger.Application }));
            session.AdditionalDiagnostics.Add(diagnostic);
            throw new CompositionException([diagnostic], session.Snapshot());
        }
        builder.WebHost.ConfigureKestrel(options =>
        {
            var loader = options.ConfigurationLoader ?? throw new InvalidOperationException("The application must configure Kestrel's named endpoint loader.");
            loader.Endpoint(name, configure);
        });
    }
}
