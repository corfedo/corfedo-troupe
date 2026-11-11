using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using static Corfedo.Troupe.Testing.Tests.TestHosts;

namespace Corfedo.Troupe.Testing.Tests.HostBuilderCallbacks;

public sealed class HostBuilderCallbackTests
{
    [Theory]
    [InlineData("build")]
    [InlineData("sources-add")]
    [InlineData("sources-clear")]
    [InlineData("sources-remove")]
    [InlineData("sources-enumerate")]
    [InlineData("sources-index")]
    [InlineData("configuration-properties")]
    [InlineData("environment-set")]
    [InlineData("child-set")]
    [InlineData("children-set")]
    [InlineData("retained-configuration-write")]
    [InlineData("retained-services")]
    [InlineData("container")]
    public void HostBuilderGuards(string misuse)
    {
        HostBuilderMisuse.Case = misuse;
        var builder = Generic(new() { ["Section:Key"] = "value" });
        var exception = Record.Exception(() =>
        {
            builder.AddTroupe<MisuseDefinition>();
            HostBuilderMisuse.AfterCallback?.Invoke();
        });
        Assert.NotNull(exception);
        Assert.True(exception is CompositionException c && c.Diagnostics.Any(d => d.Code == "TRP023") ||
            exception is InvalidOperationException, exception.ToString());
        Assert.Equal("value", builder.Configuration["Section:Key"]);
        Assert.NotEqual("Mutated", builder.Environment.EnvironmentName);
    }

    [Fact]
    public void ChainedSourceCannotExposeWritableConfiguration()
    {
        var builder = Generic();
        builder.Configuration.AddConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Guarded"] = "original" }).Build());
        var exception = Assert.Throws<CompositionException>(() => builder.AddTroupe<ChainedMutationDefinition>());
        Assert.Contains(exception.Diagnostics, d => d.Code == "TRP023");
        Assert.Equal("original", builder.Configuration["Guarded"]);
    }

    [Fact]
    public void HostBuilderReadsBindAndRegisterThroughTheOwner()
    {
        var builder = Generic(new() { ["Section:Key"] = "value", ["Section:List:0"] = "x", ["Section:List:1"] = "y", ["ConnectionStrings:db"] = "cs" });
        builder.AddTroupe<ReadingDefinition>();
        using var host = builder.Build();
        var read = host.Services.GetRequiredService<ReadValues>();
        Assert.Equal(("value", "cs", 2, "Production"), (read.Key, read.ConnectionString, read.List, read.Environment));
        var owned = host.Services.GetRequiredService<IComposition>().Manifest.Services.Where(s => s.Owner.Type == typeof(ReadingComponent)).ToArray();
        Assert.Contains(owned, s => s.ServiceType == typeof(ReadValues));
        Assert.Contains(owned, s => s.ServiceType == typeof(ILoggerProvider));
    }

    [Fact]
    public void HostBuilderCannotRemoveAnotherOwnersRegistration()
    {
        var failure = Assert.Throws<CompositionException>(() => Generic().AddTroupe<ForeignRemovalDefinition>());
        Assert.Contains(failure.Diagnostics, d => d.Code == "TRP003");
    }

    [Fact]
    public void HostBuilderCallbackFailureNamesTheComponent()
    {
        var failure = Assert.Throws<CompositionException>(() => Generic().AddTroupe<ThrowingHostBuilderDefinition>());
        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal("TRP017", diagnostic.Code);
        Assert.Contains(diagnostic.Owners, o => o.Type == typeof(ThrowingHostBuilderComponent));
    }

    [Theory]
    [InlineData(typeof(IHostApplicationBuilder))]
    [InlineData(typeof(HostApplicationBuilder))]
    [InlineData(typeof(WebApplicationBuilder))]
    public void HostBuilderContributionsAreRejected(Type target)
    {
        ContributingComponent.Target = target;
        var failure = Assert.Throws<CompositionException>(() => Generic().AddTroupe<ContributingDefinition>());
        Assert.Contains(failure.Diagnostics, d => d.Code == "TRP023" && d.Message.Contains("HostBuilder"));
    }
}

public class Core;
public sealed class Marker;
public sealed class ServiceComponent : IComponent { public void Compose(IComponentBuilder c) => c.Services(s => s.AddSingleton<Core>()); }
public static class HostBuilderMisuse
{
    public static string Case = "";
    public static Action? AfterCallback;
}
public sealed class MisuseDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<MisuseComponent>(); }
public sealed class MisuseComponent : IComponent
{
    public void Compose(IComponentBuilder c) => c.HostBuilder(b =>
    {
        HostBuilderMisuse.AfterCallback = null;
        switch (HostBuilderMisuse.Case)
        {
            case "build": b.Configuration.Build()["Section:Key"] = "mutated"; break;
            case "sources-add": b.Configuration.Sources.Add(new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource()); break;
            case "sources-clear": b.Configuration.Sources.Clear(); break;
            case "sources-remove": b.Configuration.Sources.RemoveAt(0); break;
            case "sources-enumerate": foreach (var source in b.Configuration.Sources) _ = source; break;
            case "sources-index": _ = b.Configuration.Sources[0]; break;
            case "configuration-properties": _ = b.Configuration.Properties.Count; break;
            case "environment-set": b.Environment.EnvironmentName = "Mutated"; break;
            case "child-set": b.Configuration.GetSection("Section").GetSection("Key").Value = "mutated"; break;
            case "children-set": b.Configuration.GetChildren().First()["Key"] = "mutated"; break;
            case "retained-configuration-write": { var retained = b.Configuration; HostBuilderMisuse.AfterCallback = () => retained["Section:Key"] = "mutated"; break; }
            case "retained-services": { var retained = b.Services; HostBuilderMisuse.AfterCallback = () => retained.AddSingleton<Marker>(); break; }
            case "container": b.ConfigureContainer(new DefaultServiceProviderFactory()); break;
        }
    });
}
public sealed class ChainedMutationDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<ChainedMutation>(); }
public sealed class ChainedMutation : IComponent
{
    public void Compose(IComponentBuilder c) => c.HostBuilder(b =>
        b.Configuration.Sources.OfType<ChainedConfigurationSource>().Single().Configuration!["Guarded"] = "mutated");
}
public sealed record ReadValues(string? Key, string? ConnectionString, int List, string Environment);
public sealed class ReadingDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<ReadingComponent>(); }
public sealed class ReadingComponent : IComponent
{
    public void Compose(IComponentBuilder c)
    {
        c.ReadsConfiguration("Section");
        c.HostBuilder(b =>
        {
            var list = b.Configuration.GetSection("Section:List").Get<string[]>() ?? [];
            b.Services.AddSingleton(new ReadValues(b.Configuration.GetSection("Section")["Key"], b.Configuration.GetConnectionString("db"), list.Length, b.Environment.EnvironmentName));
            b.Logging.AddProvider(new NullLoggerProviderStub());
            b.Properties["Tests.Marker"] = true;
        });
    }
}
public sealed class NullLoggerProviderStub : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public void Dispose() { }
}
public sealed class ForeignRemovalDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<ServiceComponent>().Requires<ForeignRemover>(); }
public sealed class ForeignRemover : IComponent
{
    public void Compose(IComponentBuilder c) { c.Requires<ServiceComponent>(); c.HostBuilder(b => b.Services.RemoveAll<Core>()); }
}
public sealed class ThrowingHostBuilderDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<ThrowingHostBuilderComponent>(); }
public sealed class ThrowingHostBuilderComponent : IComponent { public void Compose(IComponentBuilder c) => c.HostBuilder(_ => throw new InvalidOperationException("callback failed")); }
public sealed class ContributingDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<ContributingComponent>(); }
public sealed class ContributingComponent : IComponent
{
    public static Type Target = typeof(IHostApplicationBuilder);
    public void Compose(IComponentBuilder c)
    {
        if (Target == typeof(IHostApplicationBuilder)) c.Contribute<IHostApplicationBuilder>(_ => { });
        else if (Target == typeof(HostApplicationBuilder)) c.Contribute<HostApplicationBuilder>(_ => { });
        else c.Contribute<WebApplicationBuilder>(_ => { });
    }
}
