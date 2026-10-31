using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Corfedo.Troupe.Examples.Operations;

/// <summary>Sends operations for one caller, in process or to the owner's internal listener.</summary>
public interface IGateway
{
    Task<TResponse> SendAsync<TResponse>(IOperation<TResponse> request, CancellationToken cancellationToken);
    /// <summary>How each declared owner, by name, is reached: local or remote.</summary>
    IReadOnlyDictionary<string, string> Routes { get; }
}

/// <summary>A caller's declared destination, registered keyed by the caller so the declaration is visible in the manifest.</summary>
public sealed record Destination<THost>(Workload Owner) where THost : IComponent;

/// <summary>Declares a caller's destinations: conditional configuration, manifest-visible destinations and the keyed gateway.</summary>
public static class Calls
{
    public static void Declare(IComponentBuilder component, Workload caller, Action<Destinations> configure)
    {
        var destinations = new Destinations(component, caller);
        configure(destinations);
        var declared = destinations.Declared.ToArray();
        component.Services(services =>
        {
            services.AddKeyedSingleton(caller, (provider, _) => new CallerChannels(caller, declared, provider));
            services.AddKeyedSingleton<IGateway>(caller, (provider, _) => new WorkloadGateway(caller, declared, provider));
        });
    }

    public sealed class Destinations
    {
        private readonly IComponentBuilder component;
        private readonly Workload caller;
        internal List<OwnerHost> Declared { get; } = [];
        internal Destinations(IComponentBuilder component, Workload caller) { this.component = component; this.caller = caller; }

        /// <summary>Calls owner, hosted by THost. Its address and this caller's identity are required while THost is not composed.</summary>
        public Destinations To<THost>(Workload owner) where THost : IComponent
        {
            component.ReadsConfigurationUnless<THost>($"Roles:Remote:{owner.Name()}");
            component.ReadsConfigurationUnless<THost>($"Workload:Identities:{caller.Name()}");
            component.ReadsConfigurationUnless<THost>("Workload:Root");
            component.Services(services => services.AddKeyedSingleton(caller, new Destination<THost>(owner)));
            Declared.Add(new(owner, typeof(THost), c => c.Includes<THost>()));
            return this;
        }
    }
}

internal sealed class WorkloadGateway : IGateway
{
    private readonly Workload caller;
    private readonly Dictionary<Workload, Func<OperationEntry, object, CancellationToken, Task<object>>> routes = [];

    public WorkloadGateway(Workload caller, OwnerHost[] declared, IServiceProvider provider)
    {
        this.caller = caller;
        var composition = provider.GetRequiredService<IComposition>();
        var names = new Dictionary<string, string>();
        // The route to each owner is chosen once, from the composition.
        foreach (var destination in declared)
        {
            if (destination.Included(composition))
            {
                var invoker = provider.GetRequiredService<OperationInvoker>();
                routes.Add(destination.Owner, (operation, request, ct) => invoker.InvokeAsync(caller, operation, request, ct));
                names.Add(destination.Owner.Name(), "local");
            }
            else
            {
                var client = provider.GetRequiredKeyedService<CallerChannels>(caller).For(destination.Owner);
                routes.Add(destination.Owner, (operation, request, ct) => SendRemoteAsync(client, operation, request, ct));
                names.Add(destination.Owner.Name(), "remote");
            }
        }
        Routes = names;
    }
    public IReadOnlyDictionary<string, string> Routes { get; }

    public async Task<TResponse> SendAsync<TResponse>(IOperation<TResponse> request, CancellationToken cancellationToken)
    {
        var operation = OperationTable.For(request.GetType());
        if (!routes.TryGetValue(operation.Owner, out var route))
            throw new InvalidOperationException($"{caller.Name()} does not declare a destination for {operation.Owner.Name()}.");
        return (TResponse)await route(operation, request, cancellationToken);
    }
    private static async Task<object> SendRemoteAsync(HttpClient client, OperationEntry operation, object request, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync($"operations/{operation.Name}", JsonContent.Create(request, operation.Request), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(operation.Response, cancellationToken)
            ?? throw new InvalidOperationException($"{operation.Name} returned no result.");
    }
}

/// <summary>A caller's HTTP/2 mutual-TLS clients to remote owners, owned and disposed by DI.</summary>
internal sealed class CallerChannels : IDisposable
{
    private readonly Dictionary<Workload, HttpClient> clients = [];
    private readonly List<X509Certificate2> certificates = [];

    public CallerChannels(Workload caller, OwnerHost[] declared, IServiceProvider provider)
    {
        var composition = provider.GetRequiredService<IComposition>();
        var remote = declared.Where(d => !d.Included(composition)).ToArray();
        if (remote.Length == 0) return;
        var configuration = provider.GetRequiredService<IConfiguration>();
        var root = provider.GetRequiredService<IHostEnvironment>().ContentRootPath;
        var identity = configuration.GetSection($"Workload:Identities:{caller.Name()}");
        // A PKCS#12 round trip gives the key a form every platform's TLS stack accepts for client authentication.
        using var pem = X509Certificate2.CreateFromPemFile(Path.Combine(root, identity["Certificate"]!), Path.Combine(root, identity["Key"]!));
        var certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        var authority = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(root, configuration["Workload:Root"]!));
        certificates.AddRange([certificate, authority]);
        foreach (var destination in remote)
        {
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            policy.CustomTrustStore.Add(authority);
            var handler = new SocketsHttpHandler
            {
                SslOptions = { ClientCertificates = new X509CertificateCollection { certificate }, CertificateChainPolicy = policy }
            };
            clients.Add(destination.Owner, new HttpClient(handler)
            {
                BaseAddress = new Uri(configuration[$"Roles:Remote:{destination.Owner.Name()}"]!.TrimEnd('/') + "/"),
                DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            });
        }
    }
    public HttpClient For(Workload owner) => clients[owner];
    public void Dispose()
    {
        foreach (var client in clients.Values) client.Dispose();
        foreach (var certificate in certificates) certificate.Dispose();
    }
}
