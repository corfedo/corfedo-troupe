using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Corfedo.Troupe.Aspire.Hosting.Tests;

public sealed class WithTroupeTests
{
    private static IResourceBuilder<ProjectResource> Shop(IDistributedApplicationBuilder builder, string name = "shop") =>
        builder.AddProject<Projects.Web>(name, o => { o.ExcludeLaunchProfile = true; o.ExcludeKestrelEndpoints = true; });
    private static IResourceBuilder<ProjectResource> Operations(IDistributedApplicationBuilder builder, string name) =>
        builder.AddProject<Projects.Operations>(name, o => { o.ExcludeLaunchProfile = true; o.ExcludeKestrelEndpoints = true; });

    [Fact]
    public async Task AListenerGetsAnEndpointAndKestrelBindsItsTargetPort()
    {
        var builder = AppHostRun.Builder();
        var shop = Shop(builder).WithTroupe();
        await using var run = await AppHostRun.StartAsync(builder);
        var snapshot = await run.RunningAsync("shop");
        var endpoint = Assert.Single(shop.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(("public", "http", true), (endpoint.Name, endpoint.UriScheme, endpoint.IsProxied));
        Assert.Contains("Green tea", await run.GetAsync("shop", "public", "/products"));
        var url = Environment(snapshot)["Kestrel__Endpoints__public__Url"];
        // DCP allocates the target port; the request through the proxy shows that Kestrel binds it.
        Assert.Matches(@"^http://localhost:\d+$", url);
    }

    [Fact]
    public async Task ADeclaredEndpointIsKept()
    {
        var port = FreePort();
        var builder = AppHostRun.Builder();
        var shop = Shop(builder).WithHttpEndpoint(port: port, name: "public", isProxied: false).WithTroupe();
        await using var run = await AppHostRun.StartAsync(builder);
        await run.RunningAsync("shop");
        var endpoint = Assert.Single(shop.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal((port, false), (endpoint.Port, endpoint.IsProxied));
        Assert.Contains("Green tea", await run.GetAsync("shop", "public", "/products"));
        Assert.Equal(port, shop.GetEndpoint("public").Port);
    }

    [Fact]
    public async Task ReferencesMadeBeforeTheEndpointExistsResolveToIt()
    {
        var builder = AppHostRun.Builder();
        var shop = Shop(builder).WithTroupe();
        var client = Shop(builder, "client").WithEnvironment("Upstream", shop.GetEndpoint("public")).WithReference(shop).WithTroupe();
        await using var run = await AppHostRun.StartAsync(builder);
        var environment = Environment(await run.RunningAsync("client"));
        var address = shop.GetEndpoint("public").Url;
        Assert.Equal(address, environment["Upstream"]);
        Assert.Equal(address, environment["services__shop__public__0"]);
        Assert.Contains("Green tea", await run.GetAsync("client", "public", "/products"));
    }

    [Fact]
    public async Task EveryProblemOfEveryResourceFailsOnceBeforeAnyResourceStarts()
    {
        var builder = AppHostRun.Builder();
        var gateway = Operations(builder, "gateway").WithTroupeProfile("gateway");
        // Literal values pass unchanged; endpoint and expression values count as present.
        Operations(builder, "api").WithTroupeProfile("api")
            .WithEnvironment("Roles__Remote__gateway", "https://gateway.example")
            .WithEnvironment("Roles__Remote__ai", gateway.GetEndpoint("internal"))
            .WithEnvironment("Workload__Root", ReferenceExpression.Create($"{gateway.GetEndpoint("internal").Property(EndpointProperty.TargetPort)}"));
        Operations(builder, "ops-bad").WithArgs("--Profile=gatewy").WithTroupe();
        Operations(builder, "hybrid").WithTroupeRoles("api", "gateway");
        var app = await builder.BuildAsync(AppHostRun.Token);
        await using (app)
        {
            var error = await Assert.ThrowsAsync<DistributedApplicationException>(() => app.StartAsync(AppHostRun.Token));
            Assert.Equal(
                """
                Troupe resources cannot start:
                  api: needs Workload__Identities__api (read by ApiCalls because AiHost and GatewayHost are not in this process). No Troupe resource runs AiHost.
                       GatewayHost runs in resource "gateway" (listeners: internal), for example:
                       .WithEnvironment("Workload__Identities__api", gateway.GetEndpoint("internal"))
                  ops-bad: TRP013: Unknown profile gatewy. Declared profiles: combined, api, gateway, ai, api-gateway.
                  hybrid: TRP013: Select a declared profile using Profile configuration, not Roles. Declared profiles: combined, api, gateway, ai, api-gateway.
                """.ReplaceLineEndings(), error.Message);
            foreach (var resource in new[] { "gateway", "api", "ops-bad", "hybrid" })
                Assert.True(!app.ResourceNotifications.TryGetCurrentState(resource, out var state) || state.Snapshot.State?.Text is null ||
                    state.Snapshot.State.Text == KnownResourceStates.NotStarted,
                    $"{resource} started.");
        }
    }

    [Fact]
    public async Task AProjectWithoutDescribeModeTimesOutWithTheReason()
    {
        var builder = AppHostRun.Builder();
        builder.AddProject<Projects.Undescribed>("legacy", o => o.ExcludeLaunchProfile = true).WithTroupe();
        var timeout = ProjectDescriber.Timeout;
        ProjectDescriber.Timeout = TimeSpan.FromSeconds(3);
        try
        {
            var app = await builder.BuildAsync(AppHostRun.Token);
            await using (app)
            {
                var error = await Assert.ThrowsAsync<DistributedApplicationException>(() => app.StartAsync(AppHostRun.Token));
                Assert.StartsWith("Troupe resources cannot start:" + System.Environment.NewLine +
                    "  legacy: the project did not describe itself within 3 seconds: it must call AddTroupe from Corfedo.Troupe ", error.Message);
                Assert.EndsWith(" or later, and reach it without waiting on external services.", error.Message);
            }
        }
        finally { ProjectDescriber.Timeout = timeout; }
    }

    [Fact]
    public async Task TheDashboardShowsTheSelectionListenersAndUnservedRoles()
    {
        var builder = AppHostRun.Builder();
        Operations(builder, "gateway").WithTroupeProfile("gateway");
        await using var run = await AppHostRun.StartAsync(builder);
        var properties = (await run.RunningAsync("gateway")).Properties.ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal("gateway", properties["troupe.profile"]);
        Assert.Equal("gateway", properties["troupe.roles"]);
        Assert.Equal("internal", properties["troupe.listeners"]);
        Assert.Equal("ai, api", properties["troupe.unservedRoles"]);
    }

    [Fact]
    public async Task ThePublishedManifestHasTheBindingsAndListensOnEveryAddress()
    {
        var output = Directory.CreateTempSubdirectory("troupe-aspire-manifest-").FullName;
        try
        {
            var builder = AppHostRun.Builder("--publisher", "manifest", "--output-path", Path.Combine(output, "aspire-manifest.json"));
            Shop(builder).WithTroupe();
            var app = await builder.BuildAsync(AppHostRun.Token);
            await using (app) await app.RunAsync(AppHostRun.Token);
            var shop = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "aspire-manifest.json"), AppHostRun.Token))
                .RootElement.GetProperty("resources").GetProperty("shop");
            var binding = shop.GetProperty("bindings").GetProperty("public");
            Assert.Equal("http", binding.GetProperty("scheme").GetString());
            Assert.Equal("http://*:{shop.bindings.public.targetPort}", shop.GetProperty("env").GetProperty("Kestrel__Endpoints__public__Url").GetString());
        }
        finally { Directory.Delete(output, true); }
    }

    private static Dictionary<string, string?> Environment(CustomResourceSnapshot snapshot) =>
        snapshot.EnvironmentVariables.ToDictionary(v => v.Name, v => v.Value);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
