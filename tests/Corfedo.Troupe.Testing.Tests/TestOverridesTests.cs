using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Corfedo.Troupe.Testing.Tests;

public sealed class TestOverridesTests
{
    private static readonly CompositionOwner Fake = new(OwnerKind.Test, "test:fake");
    public TestOverridesTests() { A.Declare = _ => { }; B.Declare = _ => { }; }
    private static HostApplicationBuilder Builder(string roles = "a")
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
        builder.Configuration["Roles"] = roles;
        return builder;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TestServicesAreTestOwnedBeforeOrAfterFinalization(bool finalizeFirst)
    {
        var builder = Builder(); builder.Compose<Definition>();
        if (finalizeFirst) builder.FinalizeComposition();
        builder.TestServices("fake", services => services.AddSingleton<IValue, Replacement>());
        var manifest = builder.FinalizeComposition();
        var added = Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Replacement));
        Assert.Equal(Fake, added.Owner);
        using var host = builder.Build();
        Assert.Same(manifest, host.Services.GetRequiredService<IComposition>().Manifest);
        Assert.IsType<Replacement>(host.Services.GetRequiredService<IValue>());
    }
    [Fact]
    public void TestServicesCannotRemoveAnotherOwnersRegistration()
    {
        A.Declare = r => r.Services(s => s.AddSingleton<IValue, Original>());
        var builder = Builder(); builder.Compose<Definition>();
        builder.TestServices("fake", services => { services.AddSingleton<Probe>(); services.RemoveAll<Probe>(); });
        var error = Assert.Throws<CompositionException>(() => builder.TestServices("fake", services => services.RemoveAll<IValue>()));
        var diagnostic = Assert.Single(error.Diagnostics, d => d.Code == "TRP003");
        Assert.Equal([Fake, new(OwnerKind.Role, "a", typeof(A))], diagnostic.Owners);
        Assert.Contains(error.Manifest!.ServiceMutations, m => m.Owner == Fake && !m.Accepted);
        Assert.Single(builder.Services, s => s.ServiceType == typeof(IValue));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplaceForTestReplacesTheActiveRegistrationInPlace(bool finalizeFirst)
    {
        A.Declare = r => r.Services(s => { s.AddSingleton<IValue, Original>(); s.AddSingleton<Probe>(); });
        var builder = Builder(); builder.Compose<Definition>();
        if (finalizeFirst) builder.FinalizeComposition();
        var index = builder.Services.ToList().FindIndex(d => d.ServiceType == typeof(IValue));
        builder.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>());
        var manifest = builder.FinalizeComposition();
        Assert.Equal(typeof(Replacement), builder.Services[index].ImplementationType);
        var original = Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Original));
        var replacement = Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Replacement));
        Assert.False(original.Active); Assert.Equal(Fake, original.RemovedBy); Assert.Equal(replacement.Id, original.ReplacedBy);
        Assert.True(replacement.Active); Assert.Equal(Fake, replacement.Owner);
        Assert.Contains(manifest.ServiceMutations, m => m.Kind == ServiceMutationKind.Replace && m.Occurrence == original.Id && m.Replacement == replacement.Id && m.Accepted);
        using var host = builder.Build();
        Assert.IsType<Replacement>(host.Services.GetRequiredService<IValue>());
    }
    [Fact]
    public void ReplaceForTestFailsForMissingOrAmbiguousTargetsAndNarrowsByOwner()
    {
        A.Declare = r => r.Services(s => s.AddKeyedSingleton<IValue, Original>("k"));
        var missing = Builder(); missing.Compose<Definition>();
        var error = Assert.Throws<CompositionException>(() => missing.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>()));
        Assert.Contains(error.Diagnostics, d => d.Code == "TRP019" && d.Message.Contains("no active registration"));
        Assert.DoesNotContain(missing.Services, s => s.ServiceType == typeof(IValue) && !s.IsKeyedService);

        A.Declare = r => r.Services(s => s.AddSingleton<IValue, Original>());
        B.Declare = r => r.Services(s => s.AddSingleton<IValue, Other>());
        var ambiguous = Builder("a,b"); ambiguous.Compose<Definition>();
        error = Assert.Throws<CompositionException>(() => ambiguous.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>()));
        Assert.Contains(error.Diagnostics, d => d.Code == "TRP019" && d.Owners.Count == 3);

        var narrowed = Builder("a,b"); narrowed.Compose<Definition>();
        narrowed.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>(), owner: typeof(B));
        var manifest = narrowed.FinalizeComposition();
        Assert.Equal(Fake, Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Other)).RemovedBy);
        Assert.True(Assert.Single(manifest.Services, s => s.ImplementationType == typeof(Original)).Active);
    }
    [Fact]
    public void OverridesAfterBuildFailWithTrp016WithoutRecordingAnOccurrence()
    {
        A.Declare = r => r.Services(s => s.AddSingleton<IValue, Original>());
        var builder = Builder(); builder.Compose<Definition>(); builder.FinalizeComposition();
        using var host = builder.Build();
        var error = Assert.Throws<CompositionException>(() => builder.ReplaceForTest<IValue>("fake", ServiceDescriptor.Singleton<IValue, Replacement>()));
        Assert.Equal("TRP016", Assert.Single(error.Diagnostics).Code);
        Assert.DoesNotContain(error.Manifest!.Services, s => s.Owner.Kind == OwnerKind.Test);
        Assert.True(Assert.Single(error.Manifest.Services, s => s.ImplementationType == typeof(Original)).Active);
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
