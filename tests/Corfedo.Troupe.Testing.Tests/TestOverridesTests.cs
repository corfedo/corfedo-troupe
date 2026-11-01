using System.Net;
using System.Net.Http;
using Corfedo.Troupe.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Corfedo.Troupe.Testing.Tests;

public sealed class TestOverridesTests
{
    private static readonly CompositionOwner Fake = new(OwnerKind.Test, "test:fake");
    public TestOverridesTests() { A.Declare = _ => { }; B.Declare = _ => { }; }
    private static HostApplicationBuilder Builder(string roles = "a")
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration["Roles"] = roles;
        return builder;
    }
    [Fact]
    public void TestServicesAreTestOwned()
    {
        var builder = Builder(); builder.AddTroupe<Definition>();
        builder.TestServices("fake", services => services.AddSingleton<IValue, Replacement>());
        using var host = builder.Build();
        var manifest = host.Services.GetRequiredService<IComposition>().Manifest;
        var added = Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Replacement));
        Assert.Equal(Fake, added.Owner);
        Assert.Same(manifest, host.Services.GetRequiredService<IComposition>().Manifest);
        Assert.IsType<Replacement>(host.Services.GetRequiredService<IValue>());
    }
    [Fact]
    public void TestServicesCannotRemoveAnotherOwnersRegistration()
    {
        A.Declare = r => r.Services(s => s.AddSingleton<IValue, Original>());
        var builder = Builder(); builder.AddTroupe<Definition>();
        builder.TestServices("fake", services => { services.AddSingleton<Probe>(); services.RemoveAll<Probe>(); });
        var error = Assert.Throws<CompositionException>(() => builder.TestServices("fake", services => services.RemoveAll<IValue>()));
        var diagnostic = Assert.Single(error.Diagnostics, d => d.Code == "TRP003");
        Assert.Equal([Fake, new(OwnerKind.Role, "a", typeof(A))], diagnostic.Owners);
        Assert.Contains(error.Manifest!.ServiceMutations, m => m.Owner == Fake && !m.Accepted);
        Assert.Single(builder.Services, s => s.ServiceType == typeof(IValue));
    }
    [Fact]
    public void ReplaceForTestReplacesTheActiveRegistrationInPlace()
    {
        A.Declare = r => r.Services(s => { s.AddSingleton<IValue, Original>(); s.AddSingleton<Probe>(); });
        var builder = Builder(); builder.AddTroupe<Definition>();
        var index = builder.Services.ToList().FindIndex(d => d.ServiceType == typeof(IValue));
        builder.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>());
        using var host = builder.Build();
        var manifest = host.Services.GetRequiredService<IComposition>().Manifest;
        Assert.Equal(typeof(Replacement), builder.Services[index].ImplementationType);
        var original = Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Original));
        var replacement = Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Replacement));
        Assert.False(original.Active); Assert.Equal(Fake, original.RemovedBy); Assert.Equal(replacement.Id, original.ReplacedBy);
        Assert.True(replacement.Active); Assert.Equal(Fake, replacement.Owner);
        Assert.Contains(manifest.ServiceMutations, m => m.Kind == ServiceMutationKind.Replace && m.Occurrence == original.Id && m.Replacement == replacement.Id && m.Accepted);
        Assert.IsType<Replacement>(host.Services.GetRequiredService<IValue>());
    }
    [Fact]
    public void ReplaceForTestFailsForMissingOrAmbiguousTargetsAndNarrowsByOwner()
    {
        A.Declare = r => r.Services(s => s.AddKeyedSingleton<IValue, Original>("k"));
        var missing = Builder(); missing.AddTroupe<Definition>();
        var error = Assert.Throws<CompositionException>(() => missing.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>()));
        Assert.Contains(error.Diagnostics, d => d.Code == "TRP019" && d.Message.Contains("no active registration"));
        Assert.DoesNotContain(missing.Services, s => s.ServiceType == typeof(IValue) && !s.IsKeyedService);

        A.Declare = r => r.Services(s => s.AddSingleton<IValue, Original>());
        B.Declare = r => r.Services(s => s.AddSingleton<IValue, Other>());
        var ambiguous = Builder("a,b"); ambiguous.AddTroupe<Definition>();
        error = Assert.Throws<CompositionException>(() => ambiguous.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>()));
        Assert.Contains(error.Diagnostics, d => d.Code == "TRP019" && d.Owners.Count == 3);

        var narrowed = Builder("a,b"); narrowed.AddTroupe<Definition>();
        narrowed.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>(), owner: typeof(B));
        using var host = narrowed.Build();
        var manifest = host.Services.GetRequiredService<IComposition>().Manifest;
        Assert.Equal(Fake, Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Other)).RemovedBy);
        Assert.True(Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Original)).Active);
    }
    [Fact]
    public void OverridesAfterBuildFailWithTrp016WithoutRecordingAnOccurrence()
    {
        A.Declare = r => r.Services(s => s.AddSingleton<IValue, Original>());
        var builder = Builder(); builder.AddTroupe<Definition>();
        using var host = builder.Build();
        var error = Assert.Throws<CompositionException>(() => builder.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>()));
        Assert.Equal("TRP016", Assert.Single(error.Diagnostics).Code);
        Assert.DoesNotContain(error.Manifest!.Services, s => s.Owner.Kind == OwnerKind.Test);
        Assert.True(Assert.Single(error.Manifest.Services, s => s.ImplementationType == typeof(Original)).Active);
    }
    [Fact]
    public void OwnerOverloadReplacesTheApplicationsRegistrationBesideAComponents()
    {
        A.Declare = r => r.Services(s => s.AddSingleton<IValue, Original>());
        var builder = Builder(); builder.Services.AddSingleton<IValue, Other>(); builder.AddTroupe<Definition>();
        builder.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>(), new CompositionOwner(OwnerKind.Application, "application"));
        using var host = builder.Build();
        var manifest = host.Services.GetRequiredService<IComposition>().Manifest;
        Assert.Equal(Fake, Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Other)).RemovedBy);
        Assert.True(Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Original)).Active);
        Assert.Equal(Fake, Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Replacement)).Owner);
    }
    [Fact]
    public void ReplacingAnAlreadyRemovedTargetFailsWithTrp019()
    {
        A.Declare = r => r.Services(s => s.AddSingleton<IValue, Original>());
        var builder = Builder(); builder.AddTroupe<Definition>();
        builder.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>(), owner: typeof(A));
        var error = Assert.Throws<CompositionException>(() =>
            builder.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Other>(), owner: typeof(A)));
        Assert.Contains(error.Diagnostics, d => d.Code == "TRP019" && d.Message.Contains("no active registration owned by"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PredicateReplacesTheTypedClientsPrimaryHandlerConfigurationIndependentOfOrder(bool handlerFirst)
    {
        A.Declare = r => r.Services(s =>
        {
            var client = s.AddHttpClient<Catalog>(Catalog.Name);
            if (handlerFirst) client.ConfigurePrimaryHttpMessageHandler(() => new Unreachable());
            client.ConfigureHttpClient(c => c.BaseAddress = new Uri("http://catalog.test/"));
            if (!handlerFirst) client.ConfigurePrimaryHttpMessageHandler(() => new Unreachable());
        });
        var builder = Builder(); builder.AddTroupe<Definition>();
        var configurations = builder.Services.Where(IsCatalogConfiguration).ToList();
        Assert.Equal(2, configurations.Count);
        var target = Assert.Single(configurations, ConfiguresPrimaryHandler);
        var index = builder.Services.IndexOf(target);
        var stub = StubHandlerConfiguration();
        builder.ReplaceForTest<IConfigureOptions<HttpClientFactoryOptions>>("fake", stub, typeof(A), ConfiguresPrimaryHandler);
        using var host = builder.Build();
        var manifest = host.Services.GetRequiredService<IComposition>().Manifest;
        Assert.Same(stub, builder.Services[index]);
        var replacement = Assert.Single(manifest.Services, s => s.Owner == Fake);
        Assert.True(replacement.Active);
        var original = Assert.Single(manifest.Services, s => !s.Active && s.ServiceType == typeof(IConfigureOptions<HttpClientFactoryOptions>));
        Assert.Equal((new CompositionOwner(OwnerKind.Role, "a", typeof(A)), Fake, replacement.Id), (original.Owner, original.RemovedBy, original.ReplacedBy));
        Assert.Contains(manifest.ServiceMutations, m => m.Kind == ServiceMutationKind.Replace && m.Occurrence == original.Id && m.Replacement == replacement.Id && m.Accepted);
        Assert.Equal(2, builder.Services.Count(IsCatalogConfiguration));
        var catalog = host.Services.GetRequiredService<Catalog>();
        Assert.Equal("stub http://catalog.test/items", await catalog.Http.GetStringAsync("items", TestContext.Current.CancellationToken));
    }
    [Fact]
    public void PredicateMatchingNoneOrSeveralFailsWithTrp019ListingTheCandidates()
    {
        A.Declare = r => r.Services(s => s.AddHttpClient<Catalog>(Catalog.Name)
            .ConfigureHttpClient(c => c.BaseAddress = new Uri("http://catalog.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => new Unreachable()));
        var none = Builder(); none.AddTroupe<Definition>();
        var error = Assert.Throws<CompositionException>(() =>
            none.ReplaceForTest<IConfigureOptions<HttpClientFactoryOptions>>("fake", StubHandlerConfiguration(), typeof(A), _ => false));
        var diagnostic = Assert.Single(error.Diagnostics, d => d.Code == "TRP019");
        Assert.Contains("0 active registrations of service", diagnostic.Message);
        Assert.Contains($"owned by {typeof(A).FullName}", diagnostic.Message);
        Assert.Contains("Candidates: occurrence", diagnostic.Message);
        Assert.Contains($"instance of {typeof(ConfigureNamedOptions<HttpClientFactoryOptions>)} Singleton owned by a", diagnostic.Message);
        Assert.Equal([Fake, new(OwnerKind.Role, "a", typeof(A))], diagnostic.Owners);
        Assert.DoesNotContain(error.Manifest!.Services, s => s.Owner == Fake);

        var several = Builder(); several.AddTroupe<Definition>();
        error = Assert.Throws<CompositionException>(() =>
            several.ReplaceForTest<IConfigureOptions<HttpClientFactoryOptions>>("fake", StubHandlerConfiguration(), typeof(A), IsCatalogConfiguration));
        diagnostic = Assert.Single(error.Diagnostics, d => d.Code == "TRP019");
        Assert.Contains("2 active registrations of service", diagnostic.Message);
        Assert.Contains("Matches: occurrence", diagnostic.Message);
        Assert.Equal(2, diagnostic.Message.Split("; occurrence").Length);
        Assert.All(error.Manifest!.Services.Where(s => s.ServiceType == typeof(IConfigureOptions<HttpClientFactoryOptions>)), s => Assert.True(s.Active));
    }
    [Fact]
    public void PredicateOnlySeesTheOwnersCandidatesAndListsFactoriesAndTypes()
    {
        A.Declare = r => r.Services(s => { s.AddSingleton<IValue, Original>(); s.AddScoped<IValue>(_ => new Other()); });
        var builder = Builder(); builder.Services.AddSingleton<IValue, Other>(); builder.AddTroupe<Definition>();
        var error = Assert.Throws<CompositionException>(() =>
            builder.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>(), typeof(A), d => d.ImplementationType == typeof(Other)));
        var message = Assert.Single(error.Diagnostics, d => d.Code == "TRP019").Message;
        Assert.Contains($"type {typeof(Original)} Singleton owned by a", message);
        Assert.Contains("factory Scoped owned by a", message);
        Assert.DoesNotContain("owned by application", message);

        var application = Builder(); application.Services.AddSingleton<IValue, Other>(); application.AddTroupe<Definition>();
        application.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>(),
            new CompositionOwner(OwnerKind.Application, "application"), d => d.ImplementationType == typeof(Other));
        using var host = application.Build();
        var manifest = host.Services.GetRequiredService<IComposition>().Manifest;
        Assert.Equal(Fake, Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Other)).RemovedBy);
        Assert.True(Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Original)).Active);
    }
    [Fact]
    public void TestServicesMayRemoveTheirOwnEarlierRegistration()
    {
        var builder = Builder(); builder.AddTroupe<Definition>();
        builder.TestServices("fake", services => services.AddSingleton<IValue, Replacement>());
        builder.TestServices("fake", services => services.RemoveAll<IValue>());
        using var host = builder.Build();
        var manifest = host.Services.GetRequiredService<IComposition>().Manifest;
        Assert.Equal(Fake, Assert.Single(manifest.Services, s => s.ServiceType == typeof(IValue)).RemovedBy);
    }
    [Fact]
    public void OverridesAfterUseCompositionFailWithTrp016()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration["Roles"] = "a";
        A.Declare = r => r.Services(s => s.AddSingleton<IValue, Original>());
        builder.AddTroupe<Definition>();
        using var application = builder.Build(); application.UseComposition();
        var error = Assert.Throws<CompositionException>(() => builder.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>()));
        Assert.Equal("TRP016", Assert.Single(error.Diagnostics).Code);
    }
    private static bool IsCatalogConfiguration(ServiceDescriptor descriptor) =>
        descriptor.ServiceType == typeof(IConfigureOptions<HttpClientFactoryOptions>) &&
        descriptor.ImplementationInstance is ConfigureNamedOptions<HttpClientFactoryOptions> { Name: Catalog.Name };
    // Applies the configuration to a probe: only the primary handler configuration adds a handler builder action.
    private static bool ConfiguresPrimaryHandler(ServiceDescriptor descriptor)
    {
        if (!IsCatalogConfiguration(descriptor)) return false;
        var probe = new HttpClientFactoryOptions();
        ((IConfigureNamedOptions<HttpClientFactoryOptions>)descriptor.ImplementationInstance!).Configure(Catalog.Name, probe);
        return probe.HttpMessageHandlerBuilderActions.Count > 0;
    }
    private static ServiceDescriptor StubHandlerConfiguration() =>
        ServiceDescriptor.Singleton<IConfigureOptions<HttpClientFactoryOptions>>(new ConfigureNamedOptions<HttpClientFactoryOptions>(
            Catalog.Name, options => options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new Stub())));
    public sealed class Catalog(HttpClient http)
    {
        internal const string Name = "catalog";
        internal HttpClient Http { get; } = http;
    }
    private sealed class Stub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"stub {request.RequestUri}") });
    }
    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The component's primary handler was not replaced.");
    }
    public sealed class Definition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<A>().Role<B>(); }
    [Role("a")] public sealed class A : IRole
    { internal static Action<IRoleBuilder> Declare = _ => { }; public void Compose(IRoleBuilder r) => Declare(r); }
    [Role("b")] public sealed class B : IRole
    { internal static Action<IRoleBuilder> Declare = _ => { }; public void Compose(IRoleBuilder r) => Declare(r); }
    public interface IValue;
    public sealed class Original : IValue;
    public sealed class Other : IValue;
    public sealed class Replacement : IValue;
    public sealed class Probe;
}
