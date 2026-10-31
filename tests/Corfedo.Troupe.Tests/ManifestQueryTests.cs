using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Corfedo.Troupe.Tests;

public sealed class ManifestQueryTests
{
    private static readonly CompositionOwner RoleA = new(OwnerKind.Role, "a", typeof(CoreTests.A));
    private static readonly CompositionOwner RoleB = new(OwnerKind.Role, "b", typeof(CoreTests.B));
    private static CompositionOwner Component<T>() => new(OwnerKind.Component, typeof(T).FullName!, typeof(T));
    public ManifestQueryTests() { CoreTests.A.Declare = _ => { }; CoreTests.B.Declare = _ => { }; }

    [Fact]
    public void ComposesAndPathToFollowRequirementsFromARole()
    {
        CoreTests.A.Declare = r => r.Requires<CoreTests.Store>();
        CoreTests.B.Declare = r => r.Requires<CoreTests.Branch>();
        var manifest = CoreTests.Builder("a,b").Compose<CoreTests.Definition>();
        Assert.True(manifest.Composes<CoreTests.Branch>()); Assert.False(manifest.Composes<CoreTests.FirstB>());
        Assert.Equal([RoleA, Component<CoreTests.Store>()], manifest.PathTo<CoreTests.Store>());
        Assert.Equal([RoleB, Component<CoreTests.Branch>()], manifest.PathTo<CoreTests.Branch>());
        Assert.Empty(manifest.PathTo<CoreTests.FirstB>());
        manifest = CoreTests.Builder("b").Compose<CoreTests.Definition>();
        Assert.Equal([RoleB, Component<CoreTests.Branch>(), Component<CoreTests.Store>()], manifest.PathTo<CoreTests.Store>());
    }
    [Fact]
    public void ServiceConfigurationAndContributionQueries()
    {
        CoreTests.A.Declare = r =>
        {
            r.ReadsConfiguration("Store:Path", required: false);
            r.Contribute<List<string>>(_ => { });
            r.Services(s => { s.AddSingleton<CoreTests.Foo>(); s.AddKeyedSingleton<CoreTests.Foo>("k"); s.AddSingleton<CoreTests.Bar>(); });
        };
        CoreTests.B.Declare = r =>
        {
            r.ReadsConfiguration("store:path", required: false);
            r.Contribute<List<string>>(_ => { });
            r.Services(s => { s.AddSingleton<CoreTests.Foo>(); s.RemoveAt(s.Count - 1); s.AddSingleton<CoreTests.Foo>(); });
        };
        var builder = CoreTests.Builder("a,b"); builder.Compose<CoreTests.Definition>(); builder.ApplyContributions(new List<string>());
        var manifest = builder.FinalizeComposition();
        Assert.Equal([RoleA, RoleB], manifest.Active(typeof(CoreTests.Foo)).Select(s => s.Owner));
        Assert.Equal(RoleA, Assert.Single(manifest.Active(typeof(CoreTests.Foo), "k")).Owner);
        Assert.Empty(manifest.Active(typeof(CoreTests.Foo), "other"));
        Assert.Equal([false, true], manifest.OwnedBy(RoleB).Select(s => s.Active));
        Assert.Equal(3, manifest.OwnedBy(RoleA).Count());
        Assert.Equal([RoleA, RoleB], manifest.Reads("STORE:PATH").Select(r => r.Owner));
        Assert.Empty(manifest.Reads("Store"));
        Assert.Equal([RoleA, RoleB], manifest.ContributorsTo<List<string>>());
        Assert.Empty(manifest.ContributorsTo<List<int>>());
    }
}
