using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Corfedo.Troupe.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Corfedo.Troupe.AspNetCore.Tests.WebTests;

namespace Corfedo.Troupe.AspNetCore.Tests;

public sealed class KestrelTests
{
    public KestrelTests() => _ = new WebTests();
    [Fact]
    public async Task PemHttp2ListenerUsesDiTrustAndRequiresClientCertificate()
    {
        using var certificates = new Certificates();
        var trust = new Trust(certificates.Client.Thumbprint);
        var callbacks = 0;
        A.Declare = r => { r.Services(s => s.AddSingleton(trust)); r.Endpoints(e => e.MapGet("/", () => "secure")); };
        var builder = TlsBuilder(certificates); builder.AddTroupe<Definition>();
        builder.ConfigureCompositionEndpoint("public", endpoint =>
        {
            callbacks++;
            var resolved = endpoint.ListenOptions.ApplicationServices.GetRequiredService<Trust>();
            Assert.Same(trust, resolved);
            endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            endpoint.HttpsOptions.ClientCertificateValidation = (cert, _, _) => resolved.Admits(cert);
        });
        await using var app = builder.Build(); app.UseComposition();
        Assert.Equal(0, callbacks);
        await app.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, callbacks);
        using var admitted = certificates.HttpClient(app, withClient: true);
        using var response = await admitted.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.True(trust.Calls > 0);
        using var anonymous = certificates.HttpClient(app, withClient: false);
        await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.GetAsync("/", TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task RawSecondCallbackReplacesMtlsButTrackedSecondCallbackIsRejected()
    {
        using var certificates = new Certificates();
        var first = 0; var second = 0;
        A.Declare = r => r.Endpoints(e => e.MapGet("/", () => "unprotected"));
        var builder = TlsBuilder(certificates); builder.AddTroupe<Definition>();
        builder.ConfigureCompositionEndpoint("public", endpoint => { first++; endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate; });
        builder.WebHost.ConfigureKestrel(o => o.ConfigurationLoader!.Endpoint("public", _ => second++));
        await using (var app = builder.Build())
        {
            app.UseComposition(); await app.StartAsync(TestContext.Current.CancellationToken);
            using var anonymous = certificates.HttpClient(app, withClient: false);
            Assert.Equal("unprotected", await anonymous.GetStringAsync("/", TestContext.Current.CancellationToken));
            Assert.Equal(0, first); Assert.Equal(1, second);
        }
        var tracked = TlsBuilder(certificates); tracked.AddTroupe<Definition>();
        tracked.ConfigureCompositionEndpoint("public", e => e.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate);
        var error = Assert.Throws<CompositionException>(() => tracked.ConfigureCompositionEndpoint("public", _ => { }));
        Assert.Contains("TRP104", error.Message);
        Assert.Throws<CompositionException>(() => tracked.Build());
    }
    [Fact]
    public async Task ContributorOwnedCallbackRunsWithDiOnlyWhenItsOwnerIsComposed()
    {
        using var certificates = new Certificates();
        var trust = new Trust(certificates.Client.Thumbprint);
        var owned = 0; var unselected = 0;
        A.Declare = r =>
        {
            r.Services(s => s.AddSingleton(trust)); r.Endpoints(e => e.MapGet("/", () => "secure"));
            r.KestrelEndpoint("public", endpoint =>
            {
                owned++;
                var resolved = endpoint.ListenOptions.ApplicationServices.GetRequiredService<Trust>();
                endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                endpoint.HttpsOptions.ClientCertificateValidation = (cert, _, _) => resolved.Admits(cert);
            });
        };
        B.Declare = r => r.KestrelEndpoint("internal", _ => unselected++);
        var builder = TlsBuilder(certificates); builder.AddTroupe<Definition>();
        await using var app = builder.Build(); app.UseComposition();
        var endpoint = Assert.Single(app.Services.GetRequiredService<IComposition>().AspNetCore().KestrelEndpoints);
        Assert.Equal(("public", OwnerKind.Role, "a"), (endpoint.Name, endpoint.CallbackOwner!.Kind, endpoint.CallbackOwner.Name));
        await app.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal((1, 0), (owned, unselected));
        using var admitted = certificates.HttpClient(app, withClient: true);
        Assert.Equal("secure", await admitted.GetStringAsync("/", TestContext.Current.CancellationToken));
        Assert.True(trust.Calls > 0);
        using var anonymous = certificates.HttpClient(app, withClient: false);
        await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.GetAsync("/", TestContext.Current.CancellationToken));
    }
    [Fact]
    public void SecondCallbackOwnerIsRejectedForContributorsAndTheApplication()
    {
        A.Declare = r => r.KestrelEndpoint("public", _ => { });
        B.Declare = r => r.KestrelEndpoint("public", _ => { });
        var contributors = Assert.Throws<CompositionException>(() => Builder("a,b").AddTroupe<Definition>());
        var diagnostic = Assert.Single(contributors.Diagnostics);
        Assert.Equal(("TRP104", "b"), (diagnostic.Code, Assert.Single(diagnostic.Owners).Name));
        Assert.Contains("callback owner a", diagnostic.Message);
        var application = Builder("a"); application.AddTroupe<Definition>();
        Assert.Contains("TRP104", Assert.Throws<CompositionException>(() => application.ConfigureCompositionEndpoint("public", _ => { })).Message);
    }
    [Fact]
    public async Task ApplicationLoaderReplacementAfterCompositionFailsAtBuildInsteadOfDroppingCallbacks()
    {
        var callbacks = 0;
        A.Declare = r => { r.KestrelEndpoint("public", _ => callbacks++); r.Endpoints(e => e.MapGet("/", () => "ok")); };
        var builder = Builder("a");
        builder.WebHost.ConfigureKestrel(o => o.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: true));
        builder.AddTroupe<Definition>();
        await using (var app = builder.Build())
        {
            app.UseComposition(); await app.StartAsync(TestContext.Current.CancellationToken);
            using var client = Client(app);
            Assert.Equal("ok", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
            Assert.Equal(1, callbacks);
        }
        var replaced = Builder("a"); replaced.AddTroupe<Definition>();
        replaced.WebHost.ConfigureKestrel(o => o.Configure(replaced.Configuration.GetSection("Kestrel"), reloadOnChange: false));
        var error = Assert.Throws<InvalidOperationException>(() => replaced.Build());
        Assert.Contains("named endpoint loader was replaced", error.Message);
        Assert.Equal(1, callbacks);
    }
    [Fact]
    public async Task CodeListenerLoadingTheConfigurationBeforeAnEndpointCallbackFailsWithTrp106()
    {
        using var certificates = new Certificates();
        var callbacks = 0;
        A.Declare = r => r.Endpoints(e => e.MapGet("/", () => "ok"));
        var early = TlsBuilder(certificates); early.AddTroupe<Definition>();
        early.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificates.Server)));
        early.ConfigureCompositionEndpoint("public", _ => callbacks++);
        var error = await Assert.ThrowsAsync<CompositionException>(async () =>
        {
            await using var app = early.Build(); app.UseComposition();
            await app.StartAsync(TestContext.Current.CancellationToken);
        });
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal(("TRP106", OwnerKind.Application), (diagnostic.Code, Assert.Single(diagnostic.Owners).Kind));
        Assert.Contains("endpoint public", diagnostic.Message);
        Assert.Equal(0, callbacks);

        var adapter = TlsBuilder(certificates); adapter.AddTroupe<Definition>();
        adapter.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificate = certificates.Server })));
        adapter.ConfigureCompositionEndpoint("public", _ => callbacks++);
        await using var started = adapter.Build(); started.UseComposition();
        await started.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, callbacks);
    }
    [Fact]
    public async Task OverlappingAddressBindsFailAtStartRatherThanFromNumericPortRules()
    {
        A.Declare = r => r.Endpoints(e => e.MapGet("/", () => "ok"));
        var firstBuilder = Builder("a"); firstBuilder.AddTroupe<Definition>();
        await using var first = firstBuilder.Build(); first.UseComposition();
        await first.StartAsync(TestContext.Current.CancellationToken);
        using var client = Client(first); var port = client.BaseAddress!.Port;
        var same = Builder("a"); same.Configuration["Kestrel:Endpoints:public:Url"] = $"http://127.0.0.1:{port}";
        same.AddTroupe<Definition>(); await using var collision = same.Build(); collision.UseComposition();
        await Assert.ThrowsAsync<IOException>(() => collision.StartAsync(TestContext.Current.CancellationToken));
        if (System.Net.Sockets.Socket.OSSupportsIPv6)
        {
            var disjoint = Builder("a"); disjoint.Configuration["Kestrel:Endpoints:public:Url"] = $"http://[::1]:{port}";
            disjoint.AddTroupe<Definition>(); await using var ipv6 = disjoint.Build(); ipv6.UseComposition();
            await ipv6.StartAsync(TestContext.Current.CancellationToken);
            using var ipv6Client = Client(ipv6);
            Assert.Equal("ok", await ipv6Client.GetStringAsync("/", TestContext.Current.CancellationToken));
        }
    }
    private static WebApplicationBuilder TlsBuilder(Certificates certificates)
    {
        var builder = Builder("a");
        builder.Configuration["Kestrel:Endpoints:public:Url"] = "https://127.0.0.1:0";
        builder.Configuration["Kestrel:Endpoints:public:Protocols"] = "Http2";
        builder.Configuration["Kestrel:Endpoints:public:Certificate:Path"] = certificates.CertificatePath;
        builder.Configuration["Kestrel:Endpoints:public:Certificate:KeyPath"] = certificates.KeyPath;
        return builder;
    }
    private sealed class Trust(string thumbprint)
    {
        internal int Calls;
        internal bool Admits(X509Certificate2 certificate) { Calls++; return certificate.Thumbprint == thumbprint; }
    }
    private sealed class Certificates : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "troupe-tls-" + Guid.NewGuid().ToString("N"));
        private readonly RSA serverKey = RSA.Create(2048);
        private readonly RSA clientKey = RSA.Create(2048);
        private readonly X509Certificate2 server;
        internal X509Certificate2 Server => server;
        internal readonly X509Certificate2 Client;
        internal string CertificatePath => Path.Combine(directory, "server.pem");
        internal string KeyPath => Path.Combine(directory, "server.key");
        internal Certificates()
        {
            Directory.CreateDirectory(directory);
            var request = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            server = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            File.WriteAllText(CertificatePath, server.ExportCertificatePem()); File.WriteAllText(KeyPath, serverKey.ExportPkcs8PrivateKeyPem());
            var client = new CertificateRequest("CN=troupe-client", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            client.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
            Client = client.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        }
        internal HttpClient HttpClient(WebApplication application, bool withClient)
        {
            using var addressClient = WebTests.Client(application);
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == server.Thumbprint
            };
            if (withClient) handler.ClientCertificates.Add(Client);
            return new HttpClient(handler) { BaseAddress = addressClient.BaseAddress, DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        }
        public void Dispose() { Client.Dispose(); server.Dispose(); clientKey.Dispose(); serverKey.Dispose(); Directory.Delete(directory, true); }
    }
}
