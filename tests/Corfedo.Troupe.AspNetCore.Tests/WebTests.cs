using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Corfedo.Troupe.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Corfedo.Troupe.AspNetCore.Tests;

public sealed class WebTests
{
    public WebTests() { A.Declare = _ => { }; B.Declare = _ => { }; Definition.StageNames = ["match", "ingress", "auth", "authorize"]; }
    internal static WebApplicationBuilder Builder(string roles = "a,b", string? contentRoot = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = contentRoot ?? AppContext.BaseDirectory,
            EnvironmentName = Environments.Production, ApplicationName = typeof(WebTests).Assembly.GetName().Name
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Configuration["Roles"] = roles;
        builder.Configuration["Kestrel:Endpoints:public:Url"] = "http://127.0.0.1:0";
        builder.Logging.ClearProviders();
        builder.Host.UseDefaultServiceProvider(o => { o.ValidateOnBuild = true; o.ValidateScopes = true; });
        builder.WebHost.ConfigureKestrel(o => o.Configure(builder.Configuration.GetSection("Kestrel"), reloadOnChange: false));
        return builder;
    }
    internal static HttpClient Client(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        return new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()) };
    }
    [Fact]
    public async Task StagesAreApplicationOrderedAndOwnerGroupsPreserveLinksFallbackAndRouteOrder()
    {
        var trace = new List<string>();
        A.Declare = r =>
        {
            r.Use("ingress", app => app.Use(async (context, next) =>
            { trace.Add("a:" + context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName); await next(context); }));
            r.Endpoints(e => { e.MapGet("/minimal", () => "minimal").WithName("minimal"); e.MapGet("/ordered", () => "first").WithOrder(0); });
        };
        B.Declare = r =>
        {
            r.Use("ingress", app => app.Use(async (context, next) => { trace.Add("b"); await next(context); }));
            r.Endpoints(e => { e.MapGet("/ordered", () => "second").WithOrder(1); e.MapFallback(() => "fallback"); });
        };
        var builder = Builder("b,a"); builder.Compose<Definition>();
        await using var app = builder.Build(); app.UseComposition();
        var runtime = app.Services.GetRequiredService<IComposition>();
        Assert.All(runtime.AspNetCore().Endpoints, e => Assert.NotNull(e.Endpoint.Metadata.GetMetadata<EndpointOwner>()));
        Assert.DoesNotContain(runtime.Manifest.Diagnostics, d => d.Code == "TRP102");
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = Client(app);
        Assert.Equal("minimal", await client.GetStringAsync("/minimal", TestContext.Current.CancellationToken));
        Assert.Equal(["a:minimal", "b"], trace); trace.Clear();
        Assert.Equal("first", await client.GetStringAsync("/ordered", TestContext.Current.CancellationToken));
        Assert.Equal("fallback", await client.GetStringAsync("/missing", TestContext.Current.CancellationToken));
        Assert.Equal("/minimal", app.Services.GetRequiredService<LinkGenerator>().GetPathByName("minimal", new { }));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseWhenRequiresApplicationOwnedSuppressionOfImplicitRootAuthentication(bool suppress)
    {
        var trace = new List<string>();
        A.Declare = r =>
        {
            r.Services(s =>
            {
                s.AddSingleton(trace);
                s.AddAuthentication("trace").AddScheme<AuthenticationSchemeOptions, TraceAuthentication>("trace", _ => { });
                s.AddAuthorization();
            });
            r.Use("ingress", app => app.Use(async (context, next) => { trace.Add("ingress"); await next(context); }));
            r.Use("auth", app => ConditionalAuthenticationAdapter.Configure(app, suppress));
            r.Use("authorize", app => app.UseAuthorization());
            r.Endpoints(e => { e.MapGet("/discover", () => "public").WithName("discover").AllowAnonymous(); e.MapGet("/private", () => "private").RequireAuthorization(); });
        };
        var builder = Builder("a"); builder.Compose<Definition>();
        await using var app = builder.Build(); app.UseComposition();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = Client(app);
        Assert.Equal("public", await client.GetStringAsync("/discover", TestContext.Current.CancellationToken));
        Assert.Equal(suppress ? ["ingress"] : new[] { "authentication", "ingress" }, trace);
        trace.Clear();
        Assert.Equal("private", await client.GetStringAsync("/private", TestContext.Current.CancellationToken));
        Assert.Equal(suppress ? ["ingress", "authentication"] : new[] { "authentication", "ingress" }, trace);
        Assert.StartsWith("10.0.", typeof(WebApplication).Assembly.GetName().Version!.ToString());
    }
    [Fact]
    public async Task RootAuthenticationIsExplicitlyPlacedAfterIngress()
    {
        var trace = new List<string>();
        A.Declare = r =>
        {
            r.Services(s => { s.AddSingleton(trace); s.AddAuthentication("trace").AddScheme<AuthenticationSchemeOptions, TraceAuthentication>("trace", _ => { }); });
            r.Use("ingress", app => app.Use(async (ctx, next) => { trace.Add("ingress"); await next(ctx); }));
            r.Use("auth", app => app.UseAuthentication());
            r.Endpoints(e => e.MapGet("/", () => "ok"));
        };
        var builder = Builder("a"); builder.Compose<Definition>(); await using var app = builder.Build(); app.UseComposition();
        await app.StartAsync(TestContext.Current.CancellationToken); using var client = Client(app);
        Assert.Equal("ok", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
        Assert.Equal(["ingress", "authentication"], trace);
    }
    [Fact]
    public async Task ParameterNamesAndOverlappingConstraintsAreAdvisoryAndAmbiguousOverHttp()
    {
        A.Declare = r => r.Endpoints(e => { e.MapGet("/amb/{x}", () => "a"); e.MapGet("/overlap/{x:int}", () => "int"); });
        B.Declare = r => r.Endpoints(e => { e.MapGet("/amb/{y}", () => "b"); e.MapGet("/overlap/{y:long}", () => "long"); });
        var builder = Builder(); builder.Compose<Definition>(); await using var app = builder.Build(); app.UseComposition();
        Assert.Equal(2, app.Services.GetRequiredService<IComposition>().Manifest.Diagnostics.Count(d => d.Code == "TRP102"));
        await app.StartAsync(TestContext.Current.CancellationToken); using var client = Client(app);
        using var ambiguous = await client.GetAsync("/amb/one", TestContext.Current.CancellationToken);
        using var overlapping = await client.GetAsync("/overlap/1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, ambiguous.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, overlapping.StatusCode);
    }
    [Fact]
    public async Task DisjointConstraintsMethodsAndHostsRemainUsable()
    {
        A.Declare = r => r.Endpoints(e =>
        {
            e.MapGet("/value/{x:int}", () => "int"); e.MapGet("/method", () => "get");
            e.MapGet("/host", () => "a").RequireHost("a.example");
        });
        B.Declare = r => r.Endpoints(e =>
        {
            e.MapGet("/value/{x:alpha}", () => "alpha"); e.MapPost("/method", () => "post");
            e.MapGet("/host", () => "b").RequireHost("b.example");
        });
        var builder = Builder(); builder.Compose<Definition>(); await using var app = builder.Build(); app.UseComposition();
        Assert.DoesNotContain(app.Services.GetRequiredService<IComposition>().Manifest.Diagnostics, d => d.Code == "TRP102");
        await app.StartAsync(TestContext.Current.CancellationToken); using var client = Client(app);
        Assert.Equal("int", await client.GetStringAsync("/value/1", TestContext.Current.CancellationToken));
        Assert.Equal("alpha", await client.GetStringAsync("/value/abc", TestContext.Current.CancellationToken));
        Assert.Equal("get", await client.GetStringAsync("/method", TestContext.Current.CancellationToken));
        using var post = await client.PostAsync("/method", null, TestContext.Current.CancellationToken);
        Assert.Equal("post", await post.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        client.DefaultRequestHeaders.Host = "b.example";
        Assert.Equal("b", await client.GetStringAsync("/host", TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task GloballyDuplicateEndpointNamesFailMapping()
    {
        A.Declare = r => r.Endpoints(e => e.MapGet("/first", () => "a").WithName("same"));
        B.Declare = r => r.Endpoints(e => e.MapGet("/second", () => "b").WithName("same"));
        var builder = Builder(); builder.Compose<Definition>(); await using var app = builder.Build();
        Assert.Contains("TRP101", Assert.Throws<CompositionException>(() => app.UseComposition()).Message);
    }
    [Fact]
    public async Task UnknownStagesAndOmittedExplicitRoutingAreRejected()
    {
        A.Declare = r => r.Use("unknown", _ => { });
        Assert.Throws<CompositionException>(() => Builder("a").Compose<Definition>());
        var builder = Builder("a"); builder.Compose<NoRoutingDefinition>(); await using var app = builder.Build();
        Assert.Contains("explicit root UseRouting", Assert.Throws<CompositionException>(() => app.UseComposition()).Message);
    }
    [Fact]
    public async Task StaticAssetsCarryOwnerMetadataAndPreserveFallbackBehavior()
    {
        A.Declare = r => r.Endpoints(e => e.MapStaticAssets());
        B.Declare = r => r.Endpoints(e => e.MapFallback(() => "fallback"));
        var builder = Builder(); builder.WebHost.UseStaticWebAssets(); builder.Compose<Definition>(); await using var app = builder.Build(); app.UseComposition();
        var endpoints = app.Services.GetRequiredService<IComposition>().AspNetCore().Endpoints;
        Assert.Contains(endpoints, e => e.Endpoint is RouteEndpoint route && route.RoutePattern.RawText == "/probe.txt" && e.Owner.Name == "a");
        await app.StartAsync(TestContext.Current.CancellationToken); using var client = Client(app);
        Assert.Equal("static asset\n", await client.GetStringAsync("/probe.txt", TestContext.Current.CancellationToken));
        Assert.Equal("fallback", await client.GetStringAsync("/missing", TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task NamedListenerConfigurationMustExactlyMatchTheSelectedProfile()
    {
        var missing = Builder("a"); missing.Configuration.Sources.Clear(); missing.Configuration.AddInMemoryCollection(); missing.Configuration["Roles"] = "a";
        Assert.Contains("TRP103", Assert.Throws<CompositionException>(() => missing.Compose<Definition>()).Message);
        var extra = Builder("a"); extra.Configuration["Kestrel:Endpoints:secret:Url"] = "http://127.0.0.1:0";
        Assert.Contains("not requested", Assert.Throws<CompositionException>(() => extra.Compose<Definition>()).Message);
        var valid = Builder("a"); valid.Compose<Definition>(); valid.ConfigureCompositionEndpoint("public", _ => { });
        Assert.Throws<CompositionException>(() => valid.ConfigureCompositionEndpoint("PUBLIC", _ => { }));
        await using var host = valid.Build();
        var manifest = host.Services.GetRequiredService<IComposition>().AspNetCore();
        Assert.Equal(OwnerKind.Application, Assert.Single(manifest.KestrelEndpoints).CallbackOwner!.Kind);
    }
    public sealed class Definition : ICompositionDefinition
    {
        internal static string[] StageNames = [];
        public void Define(ICompositionDefinitionBuilder d) { d.Role<A>().Role<B>(); d.Stages(StageNames); }
    }
    public sealed class NoRoutingDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) { d.Role<NoRouting>(); d.Stages("ingress"); } }
    [Role("a")] public sealed class NoRouting : IRole { public void Compose(IRoleBuilder r) { r.KestrelEndpoint("public"); r.Endpoints(e => e.MapGet("/", () => "ok")); } }
    [Role("a")] public sealed class A : IRole
    { internal static Action<IRoleBuilder> Declare = _ => { }; public void Compose(IRoleBuilder r) { r.Requires<Ingress>(); Declare(r); } }
    [Role("b")] public sealed class B : IRole
    { internal static Action<IRoleBuilder> Declare = _ => { }; public void Compose(IRoleBuilder r) { r.Requires<Ingress>(); Declare(r); } }
    public sealed class Ingress : IComponent
    {
        public void Compose(IComponentBuilder c) { c.KestrelEndpoint("public"); c.Use("match", app => app.UseRouting()); }
    }
    private static class ConditionalAuthenticationAdapter
    {
        // ASP.NET Core 10 root marker: branch UseAuthentication does not suppress automatic root insertion.
        internal static void Configure(IApplicationBuilder app, bool suppress)
        {
            if (suppress) app.Properties["__AuthenticationMiddlewareSet"] = true;
            app.UseWhen(ctx => ctx.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName != "discover", branch => branch.UseAuthentication());
        }
    }
    private sealed class TraceAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, List<string> trace)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            trace.Add("authentication");
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test")], Scheme.Name)), Scheme.Name)));
        }
    }
}
