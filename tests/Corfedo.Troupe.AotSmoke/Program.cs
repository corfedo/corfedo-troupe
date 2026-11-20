using Corfedo.Troupe;
using Corfedo.Troupe.AspNetCore;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

// Runs Troupe in a Native AOT binary. The first host selects the "all" profile of two roles, binds options with a nested
// object and a list through the caller's binder, validates them, composes a slot that two components fill and one
// consumer takes as IEnumerable<T> (no TRP001), runs a middleware stage on a named Kestrel endpoint and answers one
// request. The second host lacks a required section and fails with TRP005. The third host has invalid options and its
// validator fails startup.
var failures = new List<string>();
void Check(bool condition, string message) { if (!condition) failures.Add(message); }

var valid = new Dictionary<string, string?>
{
    ["Logging:LogLevel:Default"] = "None",
    ["Profile"] = "all",
    ["Kestrel:Endpoints:public:Url"] = "http://127.0.0.1:0",
    ["Feed:Name"] = "npm",
    ["Feed:Retry:Count"] = "3",
    ["Feed:Tags:0"] = "a",
    ["Feed:Tags:1"] = "b",
};

var builder = WebApplication.CreateSlimBuilder();
builder.Configuration.AddInMemoryCollection(valid);
var manifest = builder.AddTroupe<SmokeComposition>();
Check(!manifest.Diagnostics.Any(d => d.Code == "TRP001"), "the IEnumerable<T> slot was reported as TRP001");
await using (var app = builder.Build())
{
    app.UseComposition();
    await app.StartAsync();
    var composition = app.Services.GetRequiredService<IComposition>();
    Check(composition.Profile?.Name == "all", $"profile {composition.Profile?.Name}");
    Check(string.Join(",", composition.Roles.Select(r => r.Name)) == "web,worker", $"roles {string.Join(",", composition.Roles.Select(r => r.Name))}");
    Check(composition.Contains<WebRole>() && composition.Contains<WorkerRole>(), "selected roles are not contained");
    Check(app.Services.GetRequiredService<FeedSources>().Names == "first,second", "the slot consumer did not get both sources");
    var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    var response = await client.GetAsync("/feed");
    var body = await response.Content.ReadAsStringAsync();
    Check(response.Headers.TryGetValues("X-Stage", out var stage) && stage.Single() == "routing", "the middleware stage did not run");
    Check(body == "npm|3|a,b", $"the endpoint answered {(int)response.StatusCode} {body}");
    await app.StopAsync();
}

var missing = WebApplication.CreateSlimBuilder();
missing.Configuration.AddInMemoryCollection(valid.Where(e => !e.Key.StartsWith("Feed", StringComparison.Ordinal)));
try
{
    missing.AddTroupe<SmokeComposition>();
    failures.Add("a missing required section did not fail");
}
catch (CompositionException e)
{
    Check(e.Diagnostics.Any(d => d.Code == "TRP005" && d.Message.Contains("Feed", StringComparison.Ordinal)), $"a missing required section failed with {e.Message}");
}

var invalid = WebApplication.CreateSlimBuilder();
invalid.Configuration.AddInMemoryCollection(valid);
invalid.Configuration["Feed:Retry:Count"] = "0";
invalid.AddTroupe<SmokeComposition>();
await using (var app = invalid.Build())
{
    app.UseComposition();
    try
    {
        await app.StartAsync();
        failures.Add("invalid options did not fail startup");
    }
    catch (OptionsValidationException e)
    {
        Check(e.Message.Contains("Retry:Count", StringComparison.Ordinal), $"invalid options failed with {e.Message}");
    }
}

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    Console.Error.WriteLine("smoke check failed");
    return 1;
}
Console.WriteLine("ok");
return 0;

public sealed class SmokeComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition)
    {
        definition.Profile("all", profile => profile.Role<WebRole>().Role<WorkerRole>());
        definition.Profile("web", profile => profile.Role<WebRole>());
        definition.Stages("routing");
    }
}

public sealed class WebRole : IRole
{
    public static string Name => "web";
    public void Compose(IRoleBuilder role) => role.Requires<IngressComponent>().Requires<FeedComponent>();
}

public sealed class WorkerRole : IRole
{
    public static string Name => "worker";
    public void Compose(IRoleBuilder role) => role.Requires<FirstSource>().Requires<SecondSource>();
}

public sealed class IngressComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.KestrelEndpoint("public");
        component.Use("routing", application =>
        {
            application.Use((context, next) => { context.Response.Headers["X-Stage"] = "routing"; return next(context); });
            application.UseRouting();
        });
        component.Endpoints(endpoints => endpoints.MapGet("/feed", (IOptions<FeedOptions> options) =>
            $"{options.Value.Name}|{options.Value.Retry.Count}|{string.Join(",", options.Value.Tags)}"));
    }
}

public sealed class FeedComponent : IComponent
{
    public void Compose(IComponentBuilder component) =>
        component.Options<FeedOptions, FeedValidator>("Feed", (section, options) => section.Bind(options));
}

public sealed class FirstSource : IComponent
{
    public void Compose(IComponentBuilder component) => component.Services(services =>
    {
        services.AddSingleton<IFeedSource>(new NamedSource("first"));
        services.AddSingleton<FeedSources>();
    });
}

public sealed class SecondSource : IComponent
{
    public void Compose(IComponentBuilder component) => component.Services(services => services.AddSingleton<IFeedSource>(new NamedSource("second")));
}

public sealed class FeedOptions
{
    public string Name { get; set; } = "";
    public RetryOptions Retry { get; set; } = new();
    public List<string> Tags { get; set; } = [];
}

public sealed class RetryOptions
{
    public int Count { get; set; }
}

public sealed class FeedValidator : IValidateOptions<FeedOptions>
{
    public ValidateOptionsResult Validate(string? name, FeedOptions options) =>
        options.Retry.Count > 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail("Feed:Retry:Count must be positive.");
}

public interface IFeedSource
{
    string Name { get; }
}

public sealed record NamedSource(string Name) : IFeedSource;

public sealed class FeedSources(IEnumerable<IFeedSource> sources)
{
    public string Names => string.Join(",", sources.Select(s => s.Name));
}
