using Corfedo.Troupe.AspNetCore;
using Corfedo.Troupe.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Corfedo.Troupe.Testing.Tests;

public sealed class VerifierTests
{
    public VerifierTests() { A.Declare = _ => { }; B.Declare = _ => { }; }
    private static HostApplicationBuilder Builder(ProfileIdentity profile)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        builder.Configuration["Roles"] = string.Join(',', profile.Roles);
        builder.Configuration["Probe:Count"] = "2";
        return builder;
    }
    [Fact]
    public async Task GenericProfilesBuildWithoutLifecycleHostedConstructorsOrArbitraryFactories()
    {
        var constructors = 0; var checks = 0; var factories = 0;
        A.Declare = r =>
        {
            r.Check((_, _) => { checks++; throw new Exception("must not run"); });
            r.Services(s =>
            {
                s.AddSingleton<IHostedService>(_ => { constructors++; throw new Exception("constructor"); });
                s.AddSingleton<Original>(_ => { factories++; throw new Exception("factory body"); });
            });
        };
        var result = await CompositionVerifier.VerifyProfilesAsync<Definition>(Builder, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString()); Assert.Equal(2, result.Profiles.Count);
        Assert.Equal(0, constructors); Assert.Equal(0, checks); Assert.Equal(0, factories);
        Assert.All(result.Profiles, p => Assert.Null(p.AspNetManifest));
    }
    [Fact]
    public async Task OverridesRecordPrivilegedRemovalAndReplacementWithTestOwner()
    {
        A.Declare = r =>
        {
            r.Options<Probe>("Probe");
            r.Services(s =>
            {
                s.AddSingleton<IValue, Original>();
                s.AddOptions<Probe>().Configure<IValue>((p, value) => p.Count = value.Count).Validate(p => p.Count == 2, "override required");
            });
        };
        var result = await CompositionVerifier.VerifyProfilesAsync<Definition>(Builder, (_, services) =>
        {
            services.RemoveAll<IValue>(); services.AddSingleton<IValue, Replacement>();
        }, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString());
        var manifest = result.Profiles.Single(p => p.Profile.Name == "a").Manifest!;
        var original = Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Original));
        Assert.False(original.Active); Assert.Equal(OwnerKind.Test, original.RemovedBy!.Kind);
        var replacement = Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Replacement));
        Assert.Equal(OwnerKind.Test, replacement.Owner.Kind);
        Assert.Contains(manifest.ServiceMutations, m => m.Occurrence == original.Id && m.Owner.Kind == OwnerKind.Test && m.Accepted);
    }
    [Fact]
    public async Task DeclaredOptionsExecuteEffectsAndAsyncResourcesAreDisposedWithoutStarting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "troupe-verifier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var created = 0; var disposed = 0;
        try
        {
            A.Declare = r =>
            {
                r.Options<Probe>("Probe");
                r.Services(s =>
                {
                    s.AddSingleton(_ => { created++; return new AsyncResource(() => disposed++); });
                    s.AddOptions<Probe>().Configure<AsyncResource>((_, _) => File.WriteAllText(Path.Combine(directory, "options.txt"), "effect"));
                });
            };
            var result = await CompositionVerifier.VerifyProfilesAsync<Definition>(Builder, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.ToString());
            Assert.Equal("effect", File.ReadAllText(Path.Combine(directory, "options.txt")));
            Assert.Equal(1, created); Assert.Equal(1, disposed);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task VerificationAndDisposalFailuresAreBothReportedAndIndependentProfilesContinue()
    {
        var disposed = 0;
        A.Declare = r =>
        {
            r.Options<Probe>("Probe");
            r.Services(s =>
            {
                s.AddSingleton(_ => new AsyncResource(() => { disposed++; throw new InvalidOperationException("cleanup failed"); }));
                s.AddOptions<Probe>().Configure<AsyncResource>((_, _) => throw new InvalidOperationException("options failed"));
            });
        };
        var result = await CompositionVerifier.VerifyProfilesAsync<Definition>(Builder, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded); Assert.Equal(2, result.Profiles.Count); Assert.Equal(2, result.Failures.Count);
        Assert.Contains(result.Failures, f => f.Profile.Name == "a" && !f.DuringDisposal && f.Exception.Message == "options failed");
        Assert.Contains(result.Failures, f => f.Profile.Name == "a" && f.DuringDisposal && f.Exception.Message == "cleanup failed");
        Assert.Equal(1, disposed); Assert.DoesNotContain(result.Failures, f => f.Profile.Name == "b");
    }
    [Fact]
    public async Task RequiredOptionsAndBuildValidationFailuresCarryProfileAndManifest()
    {
        A.Declare = r => r.Options<Probe>("Absent");
        B.Declare = r => r.Services(s => s.AddSingleton<NeedsMissing>());
        var result = await CompositionVerifier.VerifyProfilesAsync<Definition>(Builder, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded); Assert.Equal(2, result.Failures.Count);
        Assert.All(result.Failures, f => Assert.NotNull(f.Manifest));
        Assert.Contains(result.Failures, f => f.Profile.Name == "a" && f.Exception is CompositionException);
        Assert.Contains(result.Failures, f => f.Profile.Name == "b" && f.Exception is AggregateException);
    }
    [Fact]
    public async Task WebProfilesMapRealPipelineAndReportDuplicateNamesWithoutStarting()
    {
        A.Declare = r => { r.Requires<Ingress>(); r.Endpoints(e => e.MapGet("/a", () => "a").WithName("same")); };
        B.Declare = r => { r.Requires<Ingress>(); r.Endpoints(e => e.MapGet("/b", () => "b").WithName("same")); };
        var result = await CompositionVerifier.VerifyProfilesAsync<WebDefinition>(profile =>
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Configuration["Kestrel:Endpoints:public:Url"] = "http://127.0.0.1:0";
            builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
            return builder;
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(result.Failures);
        var combined = Assert.Single(result.Failures);
        Assert.Equal("both", combined.Profile.Name); Assert.Contains("TRP101", combined.Exception.Message);
        Assert.Equal(2, result.Profiles.Single(p => p.Profile.Name == "both").AspNetManifest!.Endpoints.Count);
        Assert.Single(result.Profiles.Single(p => p.Profile.Name == "single").AspNetManifest!.Endpoints);
    }
    [Fact]
    public async Task PrecomposedBuilderCanApplyApplicationAdaptersAndUnconsumedContributionsFail()
    {
        A.Declare = r => r.Contribute<AdapterBuilder>(b => b.Values.Add("a"));
        var applied = await CompositionVerifier.VerifyProfilesAsync<Definition>(profile =>
        {
            var builder = Builder(profile); builder.Compose<Definition>();
            var target = new AdapterBuilder(); builder.ApplyContributions(target);
            builder.Services.AddSingleton(target);
            return builder;
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(applied.Succeeded, applied.ToString());
        Assert.All(applied.Profiles.SelectMany(p => p.Manifest!.Contributions), c => Assert.True(c.Applied));
        Assert.All(applied.Profiles.SelectMany(p => p.Manifest!.Services).Where(s => s.ServiceType == typeof(AdapterBuilder)), s => Assert.Equal(OwnerKind.Application, s.Owner.Kind));
        var unconsumed = await CompositionVerifier.VerifyProfilesAsync<Definition>(Builder, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("TRP008", Assert.Single(unconsumed.Failures).Exception.Message);
    }
    [Fact]
    public async Task PrecomposedWrongProfileAndNoDeclaredProfilesAreRejected()
    {
        var wrong = await CompositionVerifier.VerifyProfilesAsync<Definition>(_ =>
        {
            var builder = Builder(new("a", ["a"])); builder.Compose<Definition>(); return builder;
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("b", Assert.Single(wrong.Failures).Profile.Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompositionVerifier.VerifyProfilesAsync<NoProfiles>(Builder, cancellationToken: TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task CancellationDisposesBuiltHostAndStopsVerification()
    {
        using var cancellation = new CancellationTokenSource();
        var disposed = 0;
        A.Declare = r =>
        {
            r.Options<Probe>("Probe");
            r.Services(s =>
            {
                s.AddSingleton(_ => new AsyncResource(() => disposed++));
                s.AddOptions<Probe>().Configure<AsyncResource>((_, _) => cancellation.Cancel());
            });
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => CompositionVerifier.VerifyProfilesAsync<Definition>(Builder, cancellationToken: cancellation.Token));
        Assert.Equal(1, disposed);
    }
    [Fact]
    public async Task AThrowingApplicationRuleCannotPreventHostDisposal()
    {
        var state = new RuleState();
        var disposed = 0;
        A.Declare = r =>
        {
            r.Options<Probe>("Probe");
            r.Services(s =>
            {
                s.AddSingleton(_ => new AsyncResource(() => disposed++));
                s.AddOptions<Probe>().Configure<AsyncResource>((_, _) => state.Throw = true);
            });
        };
        RuleDefinition.State = state;
        var result = await CompositionVerifier.VerifyProfilesAsync<RuleDefinition>(Builder, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded); Assert.Equal(1, disposed);
        Assert.Contains(result.Failures, f => f.Exception.InnerException?.Message == "rule failed");
    }
    [Fact]
    public async Task IncludesAndConditionalReadsAreEvaluatedPerProfile()
    {
        var included = new Dictionary<string, bool>();
        A.Declare = r =>
        {
            r.Requires<Remote>(); r.Options<Probe>("Probe");
            r.Services(s => s.AddOptions<Probe>().Configure<IComposition>((_, c) => included["a"] = c.Includes<Remote>()));
        };
        B.Declare = r =>
        {
            r.ReadsConfigurationUnless<Remote>("Remote"); r.Options<Probe>("Probe");
            r.Services(s => s.AddOptions<Probe>().Configure<IComposition>((_, c) => included["b"] = c.Includes<Remote>()));
        };
        var result = await CompositionVerifier.VerifyProfilesAsync<Definition>(Builder, cancellationToken: TestContext.Current.CancellationToken);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("b", failure.Profile.Name);
        Assert.Contains(failure.Manifest!.Diagnostics, d => d.Code == "TRP005" && d.Message.Contains(typeof(Remote).FullName!));
        Assert.Equal(new Dictionary<string, bool> { ["a"] = true }, included);
        result = await CompositionVerifier.VerifyProfilesAsync<Definition>(profile =>
        {
            var builder = Builder(profile); builder.Configuration["Remote:Address"] = "https://remote"; return builder;
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString());
        Assert.Equal(new Dictionary<string, bool> { ["a"] = true, ["b"] = false }, included);
    }
    [Fact]
    public async Task ProviderFactorySuppliedByApplicationIsPreserved()
    {
        var factory = new RecordingProviderFactory();
        var result = await CompositionVerifier.VerifyProfilesAsync<Definition>(profile =>
        {
            var builder = Builder(profile); builder.ConfigureContainer(factory); return builder;
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString()); Assert.Equal(2, factory.Builders); Assert.Equal(2, factory.Providers);
    }
    [Fact]
    public async Task MissingRoutingFailureManifestIncludesItsCause()
    {
        A.Declare = r => r.Endpoints(e => e.MapGet("/", () => "ok"));
        var result = await CompositionVerifier.VerifyProfilesAsync<Definition>(profile =>
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
            return builder;
        }, cancellationToken: TestContext.Current.CancellationToken);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("a", failure.Profile.Name);
        Assert.Contains(failure.Manifest!.Diagnostics, d => d.Code == "TRP100" && d.Message.Contains("UseRouting"));
    }
    [Fact]
    public async Task GenericVerificationCannotIgnoreDeclaredWebPipeline()
    {
        A.Declare = r => r.Endpoints(e => e.MapGet("/", () => "ok"));
        var result = await CompositionVerifier.VerifyProfilesAsync<Definition>(Builder, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(Assert.Single(result.Failures).Manifest!.Diagnostics, d => d.Code == "TRP105");
    }
    private sealed class RecordingProviderFactory : IServiceProviderFactory<IServiceCollection>
    {
        private readonly DefaultServiceProviderFactory inner = new(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        internal int Builders;
        internal int Providers;
        public IServiceCollection CreateBuilder(IServiceCollection services) { Builders++; return inner.CreateBuilder(services); }
        public IServiceProvider CreateServiceProvider(IServiceCollection services) { Providers++; return inner.CreateServiceProvider(services); }
    }
    public sealed class RuleState { public bool Throw; }
    public sealed class RuleDefinition : ICompositionDefinition
    {
        internal static RuleState State = new();
        public void Define(ICompositionDefinitionBuilder d) => d.Role<A>().Profile("a", "a").Rule(new ThrowRule(State));
    }
    private sealed class ThrowRule(RuleState state) : ICompositionRule
    {
        public IEnumerable<CompositionDiagnostic> Evaluate(CompositionManifest manifest) =>
            state.Throw ? throw new InvalidOperationException("rule failed") : [];
    }
    public sealed class Definition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<A>().Role<B>().Profile("a", "a").Profile("b", "b"); }
    public sealed class NoProfiles : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<A>(); }
    public sealed class WebDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d)
        { d.Role<A>().Role<B>().Profile("both", "a", "b").Profile("single", "a"); d.Stages("routing"); }
    }
    [Role("a")] public sealed class A : IRole
    {
        internal static Action<IRoleBuilder> Declare = _ => { };
        public void Compose(IRoleBuilder r) { Declare(r); }
    }
    [Role("b")] public sealed class B : IRole
    {
        internal static Action<IRoleBuilder> Declare = _ => { };
        public void Compose(IRoleBuilder r) { Declare(r); }
    }
    public sealed class Ingress : IComponent
    { public void Compose(IComponentBuilder c) { c.Use("routing", a => a.UseRouting()); c.KestrelEndpoint("public"); } }
    public sealed class Remote : IComponent { public void Compose(IComponentBuilder c) { } }
    public sealed class Probe { public int Count { get; set; } }
    public interface IValue { int Count { get; } }
    public sealed class Original : IValue { public int Count => 0; }
    public sealed class Replacement : IValue { public int Count => 2; }
    public sealed class NeedsMissing(Missing missing) { public Missing Value { get; } = missing; }
    public sealed class Missing;
    public sealed class AdapterBuilder { public List<string> Values { get; } = []; }
    private sealed class AsyncResource(Action dispose) : IAsyncDisposable
    { public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; } }
}
