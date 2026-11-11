using System.Diagnostics;
using System.Text.Json;
using Corfedo.Troupe.Examples.Operations;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Corfedo.Troupe.Examples.Tests;

public sealed class DescribeModeTests : IDisposable
{
    private const string Calls = "Corfedo.Troupe.Examples.Operations.ApiCalls";
    private const string Gateway = "Corfedo.Troupe.Examples.Operations.GatewayHost";
    private const string Ai = "Corfedo.Troupe.Examples.Operations.AiHost";
    private readonly string directory = Directory.CreateTempSubdirectory("troupe-describe-").FullName;

    public void Dispose() => Directory.Delete(directory, true);

    public static TheoryData<string, string[], string[], string[]> Profiles => new()
    {
        { "combined", ["ai", "api", "gateway"], ["internal", "public"], ["Internal"] },
        { "api", ["api"], ["public"], ["Roles:Remote:ai", "Roles:Remote:gateway", "Workload:Identities:api", "Workload:Root"] },
        { "gateway", ["gateway"], ["internal"], ["Internal"] },
        { "ai", ["ai"], ["internal"], ["Internal"] },
        { "api-gateway", ["api", "gateway"], ["internal", "public"], ["Internal", "Roles:Remote:ai", "Workload:Identities:api", "Workload:Root"] },
    };

    [Theory, MemberData(nameof(Profiles))]
    public void EachProfileDescribesItsRolesListenersAndNeededSections(string profile, string[] roles, string[] listeners, string[] sections)
    {
        var description = Describe("--profile", profile);
        Assert.Equal(1, description.GetProperty("format").GetInt32());
        Assert.Equal(typeof(OperationsComposition).FullName, description.GetProperty("definition").GetString());
        Assert.Equal(["ai", "api", "api-gateway", "combined", "gateway"], Names(description.GetProperty("profiles")));
        Assert.Equal(["ai", "api", "gateway"], Strings(description.GetProperty("roles")));
        var selection = description.GetProperty("selection");
        Assert.Equal(profile, selection.GetProperty("profile").GetString());
        Assert.Equal(roles, Strings(selection.GetProperty("roles")));
        Assert.Equal(listeners, Names(description.GetProperty("listeners")));
        Assert.Equal(sections, description.GetProperty("configuration").EnumerateArray().Select(c => c.GetProperty("section").GetString()!));
        Assert.Equal(sections.Length > 1, description.GetProperty("diagnostics").EnumerateArray().Any(d => d.GetProperty("code").GetString() == "TRP005"));
    }

    [Fact]
    public void ComponentsListenerOwnersAndConditionalSectionsNameTheirTypes()
    {
        var description = Describe("--profile", "api");
        var components = Strings(description.GetProperty("components"));
        Assert.Contains(Calls, components); Assert.Contains("Corfedo.Troupe.Examples.Operations.PublicIngress", components);
        Assert.DoesNotContain(Gateway, components);
        var listener = Assert.Single(description.GetProperty("listeners").EnumerateArray());
        Assert.Equal(["Corfedo.Troupe.Examples.Operations.PublicIngress"], Strings(listener.GetProperty("owners")));
        var sections = description.GetProperty("configuration").EnumerateArray().ToDictionary(c => c.GetProperty("section").GetString()!);
        Assert.Equal([Gateway], Strings(sections["Roles:Remote:gateway"].GetProperty("unlessComposed")));
        Assert.Equal([Calls], Strings(sections["Roles:Remote:gateway"].GetProperty("owners")));
        Assert.Equal([Ai, Gateway], Strings(sections["Workload:Root"].GetProperty("unlessComposed")));
        Assert.All(sections.Values, s => Assert.False(s.GetProperty("present").GetBoolean()));

        var combined = Describe("--profile", "combined");
        var internalSection = Assert.Single(combined.GetProperty("configuration").EnumerateArray());
        Assert.False(internalSection.TryGetProperty("unlessComposed", out _));
        Assert.True(internalSection.GetProperty("present").GetBoolean());
        Assert.Contains(Gateway, Strings(combined.GetProperty("components")));
    }

    [Fact]
    public void ListenersSayWhetherTheyAreConfiguredAndConfiguredEndpointsNobodyRequestsAreListed()
    {
        var description = Describe("--profile", "combined", "--Kestrel:Endpoints:public:Url=http://localhost:0", "--Kestrel:Endpoints:admin:Url=http://localhost:0");
        Assert.Equal([("internal", false), ("public", true)],
            description.GetProperty("listeners").EnumerateArray().Select(l => (l.GetProperty("name").GetString()!, l.GetProperty("configured").GetBoolean())));
        Assert.Equal(["admin"], Strings(description.GetProperty("unrequestedListeners")));
        // TRP103 restates both: one diagnostic for the listener that is not configured and one for the endpoint nobody requests.
        Assert.Equal(2, description.GetProperty("diagnostics").EnumerateArray().Count(d => d.GetProperty("code").GetString() == "TRP103"));
        Assert.Empty(Describe("--profile", "combined").GetProperty("unrequestedListeners").EnumerateArray());
    }

    [Theory]
    [InlineData(new[] { "--profile", "nope" }, "Unknown profile nope. Declared profiles: combined, api, gateway, ai, api-gateway.")]
    [InlineData(new[] { "--roles", "api" }, "Select a declared profile using Profile configuration, not Roles.")]
    public void AFailedSelectionIsDescribedWithoutASelection(string[] args, string message)
    {
        var description = Describe(args);
        Assert.Equal(JsonValueKind.Null, description.GetProperty("selection").ValueKind);
        Assert.Equal(5, description.GetProperty("profiles").GetArrayLength());
        Assert.Empty(description.GetProperty("components").EnumerateArray());
        Assert.Empty(description.GetProperty("listeners").EnumerateArray());
        Assert.Empty(description.GetProperty("unrequestedListeners").EnumerateArray());
        Assert.Empty(description.GetProperty("configuration").EnumerateArray());
        var diagnostic = Assert.Single(description.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("TRP013", diagnostic.GetProperty("code").GetString());
        Assert.Equal("Error", diagnostic.GetProperty("severity").GetString());
        Assert.StartsWith(message, diagnostic.GetProperty("message").GetString());
    }

    [Fact]
    public void TheDescriptionReplacesAnExistingFileWithoutLeavingTemporaryFiles()
    {
        var output = Path.Combine(directory, "description.json");
        File.WriteAllText(output, "stale");
        DescribeTo(output, "--profile", "gateway");
        Assert.Equal("gateway", JsonDocument.Parse(File.ReadAllText(output)).RootElement.GetProperty("selection").GetProperty("profile").GetString());
        Assert.Equal([output], Directory.GetFiles(directory));
    }

    [Fact]
    public async Task TheProcessDescribesItsOwnEnvironmentAndExitsBeforeServing()
    {
        var output = Path.Combine(directory, "process.json");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            Environment = { ["TROUPE_DESCRIBE"] = output, ["Roles__Remote__gateway"] = "https://gateway.invalid" }
        };
        foreach (var argument in new[] { ApplicationExamplesTests.ExampleExecutable("Operations"), "--profile", "api" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stdout + Environment.NewLine + await stderr);
            // Program.cs builds and runs the host after AddTroupe; Kestrel would log its listening addresses.
            Assert.DoesNotContain("Now listening", await stdout);
            Assert.Contains($"TROUPE_DESCRIBE is set: Troupe wrote this process's composition to {output}", await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None); await Task.WhenAll(stdout, stderr);
        }
        var sections = JsonDocument.Parse(await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken)).RootElement
            .GetProperty("configuration").EnumerateArray().ToDictionary(c => c.GetProperty("section").GetString()!, c => c.GetProperty("present").GetBoolean());
        Assert.True(sections["Roles:Remote:gateway"]);
        Assert.False(sections["Roles:Remote:ai"]);
    }

    private JsonElement Describe(params string[] args) => DescribeTo(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), args);

    // In process, the description ends the composition with an exception instead of the process exit.
    private static JsonElement DescribeTo(string output, params string[] args)
    {
        var codeAfterAddTroupeRan = false;
        CompositionDescription.Override.Value = new(output, () => throw new DescribedException());
        try
        {
            Assert.Throws<DescribedException>(() =>
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = ApplicationExamplesTests.ContentRoot("Operations") });
                builder.AddTroupe<OperationsComposition>();
                codeAfterAddTroupeRan = true;
            });
        }
        finally { CompositionDescription.Override.Value = null; }
        Assert.False(codeAfterAddTroupeRan);
        return JsonDocument.Parse(File.ReadAllText(output)).RootElement.Clone();
    }

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToArray();
    private static string[] Names(JsonElement array) => array.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToArray();
    private sealed class DescribedException : Exception;
}
