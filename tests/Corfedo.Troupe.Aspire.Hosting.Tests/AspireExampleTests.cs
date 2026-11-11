extern alias example;
using System.Net.Http.Json;
using Aspire.Hosting.Testing;
using Xunit;

namespace Corfedo.Troupe.Aspire.Hosting.Tests;

public sealed class AspireExampleTests
{
    [Theory]
    [InlineData("combined", "operations", "local")]
    [InlineData("split", "api", "remote")]
    public async Task TheOperationsAppHostServesWorkspacesInEachTopology(string topology, string api, string route)
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<example::Projects.AppHost>(
            ["--Topology=" + topology, "--ASPIRE_ALLOW_UNSECURED_TRANSPORT=true"], AppHostRun.Token);
        await using var run = await AppHostRun.StartAsync(builder);
        await run.RunningAsync(api);
        var workspace = await run.GetAsync(api, "public", "/workspaces/w1");
        Assert.Contains("\"summary\":\"Workspace w1 holds\"", workspace);
        using var client = run.App.CreateHttpClient(api, "public");
        Assert.Equal(new Dictionary<string, string> { ["gateway"] = route, ["ai"] = route },
            await client.GetFromJsonAsync<Dictionary<string, string>>("/routes", AppHostRun.Token));
    }
}
