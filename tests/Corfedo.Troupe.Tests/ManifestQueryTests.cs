using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Corfedo.Troupe.Tests;

public sealed class ManifestQueryTests
{
    private static readonly CompositionOwner Reader = new(OwnerKind.Role, "reader", typeof(ReaderRole));
    private static readonly CompositionOwner Writer = new(OwnerKind.Role, "writer", typeof(WriterRole));
    private static CompositionOwner Component<T>() => new(OwnerKind.Component, typeof(T).FullName!, typeof(T));
    private static HostApplicationBuilder Builder(string roles)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration["Roles"] = roles;
        return builder;
    }

    [Fact]
    public void ComposesAndPathToFollowRequirementsFromARole()
    {
        var manifest = Builder("reader,writer").AddTroupe<Definition>();
        Assert.True(manifest.Composes<Cache>()); Assert.False(manifest.Composes<Unused>());
        Assert.Equal([Reader, Component<Store>()], manifest.PathTo<Store>());
        Assert.Equal([Writer, Component<Journal>()], manifest.PathTo<Journal>());
        Assert.Empty(manifest.PathTo<Unused>());
    }
    [Fact]
    public void PathToResolvesEqualLengthChainsOrdinally()
    {
        // Writer reaches Store through Cache and through Journal; Cache sorts first.
        var manifest = Builder("writer").AddTroupe<Definition>();
        Assert.Equal([Writer, Component<Cache>(), Component<Store>()], manifest.PathTo<Store>());
    }
    [Fact]
    public void ServiceConfigurationAndContributionQueries()
    {
        var builder = Builder("reader,writer"); builder.AddTroupe<Definition>(); builder.ApplyContributions(new List<string>());
        using var host = builder.Build();
        var manifest = host.Services.GetRequiredService<IComposition>().Manifest;
        Assert.Equal([Reader, Writer], manifest.Active(typeof(Value)).Select(s => s.Owner));
        Assert.Equal(Reader, Assert.Single(manifest.Active(typeof(Value), "k")).Owner);
        Assert.Empty(manifest.Active(typeof(Value), "other"));
        Assert.Equal([false, true], manifest.OwnedBy(Writer).Where(s => s.ServiceType == typeof(Value)).Select(s => s.Active));
        Assert.Equal(2, manifest.OwnedBy(Reader).Count());
        Assert.Equal([Reader, Writer], manifest.Reads("STORE:PATH").Select(r => r.Owner));
        Assert.Empty(manifest.Reads("Store"));
        Assert.Equal([Reader, Writer], manifest.ContributorsTo<List<string>>());
        Assert.Empty(manifest.ContributorsTo<List<int>>());
    }
    public sealed class Definition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<ReaderRole>().Role<WriterRole>(); }
    [Role("reader")] public sealed class ReaderRole : IRole
    {
        public void Compose(IRoleBuilder r)
        {
            r.Requires<Store>();
            r.ReadsConfiguration("Store:Path", required: false);
            r.Contribute<List<string>>(_ => { });
            r.Services(s => { s.AddSingleton<Value>(); s.AddKeyedSingleton<Value>("k"); });
        }
    }
    [Role("writer")] public sealed class WriterRole : IRole
    {
        public void Compose(IRoleBuilder r)
        {
            r.Requires<Journal>().Requires<Cache>();
            r.ReadsConfiguration("store:path", required: false);
            r.Contribute<List<string>>(_ => { });
            r.Services(s => { s.AddSingleton<Value>(); s.RemoveAt(s.Count - 1); s.AddSingleton<Value>(); });
        }
    }
    public sealed class Store : IComponent { public void Compose(IComponentBuilder c) { } }
    public sealed class Cache : IComponent { public void Compose(IComponentBuilder c) => c.Requires<Store>(); }
    public sealed class Journal : IComponent { public void Compose(IComponentBuilder c) => c.Requires<Store>(); }
    public sealed class Unused : IComponent { public void Compose(IComponentBuilder c) { } }
    public sealed class Value;
}
