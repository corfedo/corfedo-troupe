using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Corfedo.Troupe.AspNetCore;
using Corfedo.Troupe.Examples.Operations;
using Corfedo.Troupe.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Corfedo.Troupe.Examples.Tests;

public sealed class OperationsExampleTests : IDisposable
{
    private readonly string certificates = Path.Combine(Path.GetTempPath(), "troupe-operations-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Unreachable = new("https://127.0.0.1:1");

    public OperationsExampleTests() => DevelopmentCertificates.Write(certificates, "api", "rogue", "other");
    public void Dispose() => Directory.Delete(certificates, true);
    private string File(string name) => Path.Combine(certificates, name);

    [Fact]
    public async Task EveryServingProfileVerifiesWithItsListenersAndConditionalReads()
    {
        var result = await CompositionVerifier.VerifyProfilesAsync<OperationsComposition>(profile =>
        {
            var roles = string.Join(',', profile.Roles);
            return Builder(roles, roles.Contains("api") ? Remote(roles.Contains("gateway") ? null : Unreachable, Unreachable) : []);
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString());
        Assert.Equal(5, result.Profiles.Count);
        var combined = result.Profiles.Single(p => p.Profile.Name == "combined");
        var listener = combined.AspNetManifest!.KestrelEndpoints.Single(e => e.Name == "internal");
        Assert.Equal(typeof(InternalListener), listener.CallbackOwner!.Type);
        Assert.Null(combined.AspNetManifest.KestrelEndpoints.Single(e => e.Name == "public").CallbackOwner);
        Assert.Equal(["public"], result.Profiles.Single(p => p.Profile.Name == "api").AspNetManifest!.KestrelEndpoints.Select(e => e.Name));
        Assert.Equal(["internal"], result.Profiles.Single(p => p.Profile.Name == "gateway").AspNetManifest!.KestrelEndpoints.Select(e => e.Name));
        var reads = result.Profiles.Single(p => p.Profile.Name == "api").Manifest!.Configuration.Where(r => r.UnlessComposed is not null).ToArray();
        Assert.Contains(reads, r => r.Section == "Roles:Remote:gateway" && r.UnlessComposed == typeof(GatewayHost));
        Assert.Contains(reads, r => r.Section == "Roles:Remote:ai" && r.UnlessComposed == typeof(AiHost));
    }
    [Fact]
    public void AnAbsentOwnerWithoutAnAddressFailsAtCompose()
    {
        var error = Assert.Throws<CompositionException>(() => Builder("api", Remote(Unreachable, null)));
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("TRP005", diagnostic.Code);
        Assert.Contains("Roles:Remote:ai", diagnostic.Message); Assert.Contains(typeof(AiHost).FullName!, diagnostic.Message);
        // In the hybrid process Gateway is local, so only AI's address is required.
        Builder("api,gateway", Remote(null, Unreachable));
    }
    [Fact]
    public async Task CombinedProcessCallsLocallyAndKeepsOperationRoutesOnTheInternalListener()
    {
        await using var combined = await StartAsync(Builder("ai,api,gateway"));
        using var api = new HttpClient { BaseAddress = Address(combined, "http") };
        Assert.Equal(new WorkspaceView("w1", "Workspace w1", "Workspace w1 holds", 6),
            await api.GetFromJsonAsync<WorkspaceView>("workspaces/w1", TestContext.Current.CancellationToken));
        Assert.Equal(Routes(("gateway", "local"), ("ai", "local")), await RoutesAsync(api));
        using var publicOperation = await api.PostAsJsonAsync("operations/workspace.get", new GetWorkspace("w1"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, publicOperation.StatusCode);
        using var caller = Client(Address(combined, "https"), "api");
        using var internalRoutes = await caller.GetAsync("routes", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, internalRoutes.StatusCode);
        using var operation = await caller.PostAsJsonAsync("operations/workspace.get", new GetWorkspace("w2"), TestContext.Current.CancellationToken);
        Assert.Equal(new Workspace("w2", "Workspace w2"), await operation.Content.ReadFromJsonAsync<Workspace>(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task SplitAndHybridProcessesCallRemoteOwnersOverMutualTls()
    {
        await using var gateway = await StartAsync(Builder("gateway"));
        await using var ai = await StartAsync(Builder("ai"));
        await using var split = await StartAsync(Builder("api", Remote(Address(gateway, "https"), Address(ai, "https"))));
        using var splitClient = new HttpClient { BaseAddress = Address(split, "http") };
        Assert.Equal(new WorkspaceView("w1", "Workspace w1", "Workspace w1 holds", 6),
            await splitClient.GetFromJsonAsync<WorkspaceView>("workspaces/w1", TestContext.Current.CancellationToken));
        Assert.Equal(Routes(("gateway", "remote"), ("ai", "remote")), await RoutesAsync(splitClient));

        await using var hybrid = await StartAsync(Builder("api,gateway", Remote(null, Address(ai, "https"))));
        using var hybridClient = new HttpClient { BaseAddress = Address(hybrid, "http") };
        Assert.Equal("Workspace w3", (await hybridClient.GetFromJsonAsync<WorkspaceView>("workspaces/w3", TestContext.Current.CancellationToken))!.Name);
        Assert.Equal(Routes(("gateway", "local"), ("ai", "remote")), await RoutesAsync(hybridClient));
    }
    [Fact]
    public async Task InternalListenerIdentifiesCallersByCertificateAndAppliesTheCallerPolicy()
    {
        await using var gateway = await StartAsync(Builder("gateway", ["--Internal:Callers:gateway", File("other.pem")]));
        var address = Address(gateway, "https");
        foreach (var untrusted in new[] { null, "rogue" })
        {
            using var client = Client(address, untrusted);
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.PostAsJsonAsync("operations/workspace.get", new GetWorkspace("w1"), TestContext.Current.CancellationToken));
        }
        // A trusted certificate that names a caller the catalogue does not allow is denied.
        using var other = Client(address, "other");
        using var denied = await other.PostAsJsonAsync("operations/workspace.get", new GetWorkspace("w1"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
    [Fact]
    public void CallerDeclarationsAreCheckedAgainstTheCatalogueInBothDirections()
    {
        var error = Assert.Throws<CompositionException>(() => Compose<IncompleteCallsComposition>("api", Remote(Unreachable, Unreachable)));
        var missing = Assert.Single(error.Diagnostics);
        Assert.Equal(("OPS001", typeof(GatewayOnlyCalls)), (missing.Code, Assert.Single(missing.Owners).Type));
        Assert.Contains("declares no destination for ai", missing.Message);
        error = Assert.Throws<CompositionException>(() => Compose<IncompleteCallsComposition>("gateway",
            ["--Roles:Remote:ai", Unreachable.ToString(), "--Workload:Identities:gateway:Certificate", "x", "--Workload:Root", "x"]));
        Assert.Contains(error.Diagnostics, d => d.Code == "OPS002" && d.Message.Contains("ai exposes no operation to it"));
    }

    private WebApplicationBuilder Builder(string roles, params string[] extra) => Compose<OperationsComposition>(roles, extra);
    private WebApplicationBuilder Compose<T>(string roles, string[] extra) where T : ICompositionDefinition, new()
    {
        var args = new List<string>
        {
            "--roles", roles,
            "--Kestrel:Certificates:Default:Path", File("server.pem"), "--Kestrel:Certificates:Default:KeyPath", File("server.key"),
            "--Internal:Callers:api", File("api.pem")
        };
        if (typeof(T) == typeof(OperationsComposition))
        {
            if (roles.Contains("api")) args.AddRange(["--Kestrel:Endpoints:public:Url", "http://127.0.0.1:0"]);
            if (roles.Contains("gateway") || roles.Contains("ai")) args.AddRange(["--Kestrel:Endpoints:internal:Url", "https://127.0.0.1:0"]);
        }
        args.AddRange(extra);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = args.ToArray(), ContentRootPath = ApplicationExamplesTests.ContentRoot("Operations") });
        builder.Logging.ClearProviders();
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
        builder.Compose<T>();
        return builder;
    }
    private string[] Remote(Uri? gateway, Uri? ai)
    {
        var args = new List<string> { "--Workload:Identities:api:Certificate", File("api.pem"), "--Workload:Identities:api:Key", File("api.key"), "--Workload:Root", File("root.pem") };
        if (gateway is not null) args.AddRange(["--Roles:Remote:gateway", gateway.ToString()]);
        if (ai is not null) args.AddRange(["--Roles:Remote:ai", ai.ToString()]);
        return args.ToArray();
    }
    private static async Task<WebApplication> StartAsync(WebApplicationBuilder builder)
    {
        builder.FinalizeComposition();
        var application = builder.Build(); application.UseComposition();
        await application.StartAsync(TestContext.Current.CancellationToken);
        return application;
    }
    private static Uri Address(WebApplication application, string scheme) => new(application.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single(a => a.StartsWith(scheme + "://", StringComparison.Ordinal)).TrimEnd('/') + "/");
    private HttpClient Client(Uri address, string? caller)
    {
        var root = X509CertificateLoader.LoadCertificateFromFile(File("root.pem"));
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        policy.CustomTrustStore.Add(root);
        var handler = new SocketsHttpHandler { SslOptions = { CertificateChainPolicy = policy } };
        if (caller is not null)
        {
            using var pem = X509Certificate2.CreateFromPemFile(File($"{caller}.pem"), File($"{caller}.key"));
            handler.SslOptions.ClientCertificates = [X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null)];
        }
        return new HttpClient(handler) { BaseAddress = address, DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
    }
    private static Dictionary<string, string> Routes(params (string Owner, string Route)[] routes) => routes.ToDictionary(r => r.Owner, r => r.Route);
    private static async Task<Dictionary<string, string>?> RoutesAsync(HttpClient client) =>
        await client.GetFromJsonAsync<Dictionary<string, string>>("routes", TestContext.Current.CancellationToken);

    public sealed class IncompleteCallsComposition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder definition)
        {
            definition.Role<IncompleteApi>().Role<IncompleteGateway>().Rule(new CallsRule());
            definition.Stages("routing", "internal");
        }
    }
    [Role("api")] public sealed class IncompleteApi : IRole { public void Compose(IRoleBuilder role) => role.Requires<GatewayOnlyCalls>(); }
    [Role("gateway")] public sealed class IncompleteGateway : IRole { public void Compose(IRoleBuilder role) => role.Requires<GatewayCallsAi>(); }
    public sealed class GatewayOnlyCalls : IComponent
    { public void Compose(IComponentBuilder component) => Calls.Declare(component, Workload.Api, d => d.To<GatewayHost>(Workload.Gateway)); }
    public sealed class GatewayCallsAi : IComponent
    { public void Compose(IComponentBuilder component) => Calls.Declare(component, Workload.Gateway, d => d.To<AiHost>(Workload.Ai)); }
}
