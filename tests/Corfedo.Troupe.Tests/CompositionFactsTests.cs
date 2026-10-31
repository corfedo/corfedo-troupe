using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Corfedo.Troupe.Tests.CoreTests;

namespace Corfedo.Troupe.Tests;

public sealed class CompositionFactsTests
{
    public CompositionFactsTests() => _ = new CoreTests();
    [Fact]
    public void IncludesReportsComposedComponentsAfterBuildBeforeStart()
    {
        A.Declare = r => r.Requires<Store>(); B.Declare = r => r.Requires<Branch>();
        var builder = Builder("a"); builder.Compose<Definition>();
        using var host = builder.Build();
        var composition = host.Services.GetRequiredService<IComposition>();
        Assert.True(composition.Includes<Store>());
        Assert.False(composition.Includes<Branch>());
        var combined = Builder("a,b"); combined.Compose<Definition>();
        using var both = combined.Build();
        Assert.True(both.Services.GetRequiredService<IComposition>().Includes<Branch>());
    }
    [Fact]
    public void ConditionalReadIsRequiredOnlyWhileItsComponentIsAbsent()
    {
        A.Declare = r => r.ReadsConfigurationUnless<Branch>("Remote:Branch");
        B.Declare = r => r.Requires<Branch>();
        var error = Assert.Throws<CompositionException>(() => Builder("a").Compose<Definition>());
        var diagnostic = Assert.Single(error.Diagnostics, d => d.Code == "TRP005");
        Assert.Equal($"a: section Remote:Branch is required because {typeof(Branch).FullName} is not composed in this process.", diagnostic.Message);
        var read = Assert.Single(error.Manifest!.Configuration);
        Assert.Equal(("Remote:Branch", false, typeof(Branch)), (read.Section, read.Required, read.UnlessComposed));
        var manifest = Builder("a,b").Compose<Definition>();
        Assert.Equal(typeof(Branch), Assert.Single(manifest.Configuration).UnlessComposed);
        var configured = Builder("a"); configured.Configuration["Remote:Branch:Address"] = "https://branch";
        configured.Compose<Definition>();
    }
    [Fact]
    public void OwnersMayDeclareTheSameSectionWithDifferentConditions()
    {
        A.Declare = r => r.ReadsConfigurationUnless<Store>("Shared");
        B.Declare = r => { r.Requires<Store>(); r.ReadsConfigurationUnless<Branch>("Shared"); };
        var error = Assert.Throws<CompositionException>(() => Builder("a,b").Compose<Definition>());
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal(("TRP005", "b"), (diagnostic.Code, Assert.Single(diagnostic.Owners).Name));
        Assert.Equal([typeof(Store), typeof(Branch)], error.Manifest!.Configuration.Select(c => c.UnlessComposed));
        B.Declare = r => { r.Requires<Branch>(); r.ReadsConfigurationUnless<Branch>("Shared"); };
        Builder("a,b").Compose<Definition>();
    }
    [Fact]
    public void ConditionalReadIsCheckedAgainAtFinalization()
    {
        A.Declare = r => r.ReadsConfigurationUnless<Branch>("Remote:Branch");
        var builder = Builder("a"); builder.Configuration["Remote:Branch:Address"] = "https://branch";
        builder.Compose<Definition>();
        builder.Configuration.Sources.Clear();
        var error = Assert.Throws<CompositionException>(() => builder.FinalizeComposition());
        Assert.Contains(error.Diagnostics, d => d.Code == "TRP005" && d.Message.Contains("Remote:Branch"));
    }
}
