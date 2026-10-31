using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe.AspNetCore;

internal sealed class KestrelComposition(IConfiguration configuration)
{
    // The loader created from this section instance is Troupe's; any other loader replaced it.
    private readonly IConfigurationSection section = configuration.GetSection("Kestrel");
    private readonly Dictionary<string, List<CompositionOwner>> requested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CompositionOwner> callbacks = new(StringComparer.OrdinalIgnoreCase);

    internal void Require(CompositionSession session, string name, CompositionOwner owner)
    {
        if (requested.Count == 0) OwnLoader(session);
        if (!requested.TryGetValue(name, out var owners)) requested.Add(name, owners = []);
        if (!owners.Contains(owner)) owners.Add(owner);
    }
    // Registered before any endpoint callback: configures the Kestrel section once without topology reload
    // and fails when a later KestrelServerOptions.Configure call replaces the loader and its callbacks.
    // Each endpoint with a callback owner starts with a placeholder that its callback replaces; the placeholder runs only
    // when the loader loads before the callback is attached, which would leave the endpoint with Kestrel's defaults.
    private void OwnLoader(CompositionSession session)
    {
        var owned = section;
        session.ConfigureServices(ServiceLedger.Application, services =>
        {
            services.Configure<KestrelServerOptions>(options =>
            {
                var loader = options.Configure(owned, reloadOnChange: false);
                foreach (var (name, owner) in callbacks) loader.Endpoint(name, _ => throw EarlyLoad(name, owner));
            });
            services.PostConfigure<KestrelServerOptions>(options => Loader(options, owned));
        });
    }
    private static CompositionException EarlyLoad(string name, CompositionOwner owner) => Errors.Fail(DiagnosticCodes.EarlyKestrelLoad,
        $"Kestrel loaded its endpoint configuration before the callback of endpoint {name} owned by {owner.Name} was attached, so the endpoint " +
        "would keep Kestrel's defaults. A code listener configured earlier called a UseHttps overload that loads the configuration " +
        "(UseHttps(), a certificate, file or store, or Action<HttpsConnectionAdapterOptions>); use UseHttps(HttpsConnectionAdapterOptions), " +
        "UseHttps(TlsHandshakeCallbackOptions) or UseHttps(ServerOptionsSelectionCallback, state).", owner);
    internal Action<KestrelServerOptions> EndpointCallback(string name, Action<EndpointConfiguration> configure)
    {
        var owned = section;
        return options => Loader(options, owned).Endpoint(name, configure);
    }
    private static KestrelConfigurationLoader Loader(KestrelServerOptions options, IConfigurationSection owned) =>
        options.ConfigurationLoader is { } loader && ReferenceEquals(loader.Configuration, owned) ? loader :
            throw new InvalidOperationException("Kestrel's named endpoint loader was replaced after composition. Troupe configures the Kestrel section when named endpoints are requested; remove the application's KestrelServerOptions.Configure call so endpoint callbacks are kept.");
    internal void RegisterCallback(CompositionSession session, string name)
    {
        session.RequireConfiguration();
        if (!requested.ContainsKey(name)) throw session.Fail("TRP103", $"Kestrel endpoint {name} is not requested.", ServiceLedger.Application);
        Own(session, name, ServiceLedger.Application);
    }
    internal void RegisterOwnedCallback(CompositionSession session, CompositionOwner owner, string name, Action<EndpointConfiguration> configure)
    {
        Require(session, name, owner);
        Own(session, name, owner);
        session.ConfigureServices(owner, services => services.Configure(EndpointCallback(name, configure)));
    }
    private void Own(CompositionSession session, string name, CompositionOwner owner)
    {
        if (!callbacks.TryAdd(name, owner))
            throw session.Fail("TRP104", $"Kestrel endpoint {name} already has callback owner {callbacks[name].Name}; a second callback would replace its policy.", owner);
    }
    internal IEnumerable<CompositionDiagnostic> Evaluate()
    {
        var configured = configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in requested)
            if (!configured.Contains(requirement.Key))
                yield return new("TRP103", DiagnosticSeverity.Error, $"Requested Kestrel endpoint {requirement.Key} is missing from Kestrel:Endpoints.", Array.AsReadOnly(requirement.Value.ToArray()));
        foreach (var extra in configured.Where(n => !requested.ContainsKey(n)))
            yield return new("TRP103", DiagnosticSeverity.Error, $"Configured Kestrel endpoint {extra} is not requested by this composition.", Array.AsReadOnly(new[] { ServiceLedger.Application }));
    }
    internal IReadOnlyList<KestrelEndpointEntry> Snapshot() => Array.AsReadOnly(requested.OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => new KestrelEndpointEntry(p.Key, Array.AsReadOnly(p.Value.ToArray()), callbacks.GetValueOrDefault(p.Key))).ToArray());
}
