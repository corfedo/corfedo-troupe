using Corfedo.Troupe.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using static Corfedo.Troupe.Testing.Tests.TestHosts;

namespace Corfedo.Troupe.Testing.Tests.OptionsBinding;

// A second configuration binding of owned options fails with TRP022 at Build.
public sealed class OwnedOptionsBindingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("configure-section", true)]
    [InlineData("configure-all", true)]
    [InlineData("options-bind", true)]
    [InlineData("bind-configuration", true)]
    [InlineData("component-second-binding", true)]
    [InlineData("lambda-bind", true)]
    [InlineData("lambda-get", true)]
    [InlineData("post-configure-bind", true)]
    [InlineData("lambda-appends-collection", true)]
    [InlineData("invalid-value-second-binding", true)]
    [InlineData("lambda-reads-owned-key", false)]
    [InlineData("lambda-binds-other-section", false)]
    [InlineData("named-configure", false)]
    [InlineData("named-bind-configuration", false)]
    [InlineData("lambda-reads-configuration", false)]
    [InlineData("custom-reload-source", false)]
    [InlineData("configuration-reload-source", false)]
    public async Task SecondBindingsOfOwnedOptions(string leftover, bool rejected)
    {
        Func<IHostApplicationBuilder> create = () =>
        {
            var builder = Generic(new() { ["Owned:Values:0"] = "a", ["Owned:Label"] = "l", ["Other:Values:0"] = "o" });
            var section = builder.Configuration.GetSection("Owned");
            _ = leftover switch
            {
                "configure-section" => builder.Services.Configure<Owned>(section),
                "configure-all" => builder.Services.Configure<Owned>((string?)null, (IConfiguration)section),
                "options-bind" => builder.Services.AddOptions<Owned>().Bind(section).Services,
                "bind-configuration" => builder.Services.AddOptions<Owned>().BindConfiguration("Owned").Services,
                "lambda-bind" => builder.Services.Configure<Owned>(o => section.Bind(o)),
                "lambda-get" => builder.Services.Configure<Owned>(o => o.Values = section.Get<Owned>()!.Values),
                "post-configure-bind" => builder.Services.PostConfigure<Owned>(o => section.Bind(o)),
                "lambda-appends-collection" => builder.Services.Configure<Owned>(o => o.Values.AddRange(section.GetSection("Values").Get<string[]>() ?? [])),
                "invalid-value-second-binding" => builder.Services.Configure<Owned>(o => { section.Bind(o); throw new FormatException("invalid value"); }),
                "lambda-reads-owned-key" => builder.Services.Configure<Owned>(o => o.Label = section["Label"] ?? ""),
                "lambda-binds-other-section" => builder.Services.Configure<Owned>(o => builder.Configuration.GetSection("Other").Bind(o)),
                "named-configure" => builder.Services.Configure<Owned>("secondary", section),
                "named-bind-configuration" => builder.Services.AddOptions<Owned>("secondary").BindConfiguration("Owned").Services,
                "lambda-reads-configuration" => builder.Services.AddOptions<Owned>().Configure<IConfiguration>((o, c) => o.Label = c["Label"] ?? "").Services,
                "custom-reload-source" => builder.Services.AddSingleton<IOptionsChangeTokenSource<Owned>>(new ManualReload()),
                "configuration-reload-source" => builder.Services.AddSingleton<IOptionsChangeTokenSource<Owned>>(
                    new ConfigurationChangeTokenSource<Owned>(new ConfigurationBuilder().AddInMemoryCollection().Build())),
                _ => builder.Services
            };
            return builder;
        };
        var result = leftover == "component-second-binding"
            ? await CompositionVerifier.VerifyAsync<SecondBindingDefinition>(create, cancellationToken: Token)
            : await CompositionVerifier.VerifyAsync<OptionsDefinition>(create, cancellationToken: Token);
        Assert.Equal(rejected, result.Failures.Any(f => f.Exception.Message.Contains("TRP022")));
        if (!rejected) Assert.True(result.Succeeded, result.ToString());
    }

    // The fixture assembly builds with the configuration binding source generator, which intercepts its binding calls.
    [Theory]
    [InlineData("bind-configuration", true)]
    [InlineData("options-bind", true)]
    [InlineData("configure-section", true)]
    [InlineData("lambda-bind", true)]
    [InlineData("named-bind-configuration", false)]
    [InlineData("named-configure", false)]
    public void SourceGeneratedSecondBindingsOfOwnedOptions(string binding, bool rejected)
    {
        var builder = Generic(new() { ["Owned:Values:0"] = "a" });
        builder.AddTroupe<GeneratedOptionsDefinition>();
        GeneratedBinding.Add(binding, builder.Services, builder.Configuration, "Owned");
        var exception = Record.Exception(() => { using var host = builder.Build(); });
        Assert.Equal(rejected, exception is CompositionException c && c.Diagnostics.Any(d => d.Code == "TRP022"));
        if (!rejected) Assert.Null(exception);
    }

    [Fact]
    public void BinderOfOwnedOptionsIsTheirOneBinding()
    {
        var builder = Generic(new() { ["Owned:Values:0"] = "a" });
        builder.AddTroupe<BinderOptionsDefinition>();
        using (var host = builder.Build())
            Assert.Equal(["a"], host.Services.GetRequiredService<IOptions<Owned>>().Value.Values);
        var second = Generic(new() { ["Owned:Values:0"] = "a" });
        second.AddTroupe<BinderSecondBindingDefinition>();
        Assert.Contains(Assert.Throws<CompositionException>(() => second.Build()).Diagnostics, d => d.Code == "TRP022");
    }

    [Fact]
    public void SecondBindingFailsAtBuild()
    {
        var builder = Generic(new() { ["Owned:Values:0"] = "a" });
        builder.AddTroupe<OptionsDefinition>();
        builder.Services.Configure<Owned>(builder.Configuration.GetSection("Owned"));
        var failure = Assert.Throws<CompositionException>(() => builder.Build());
        Assert.Contains(failure.Diagnostics, d => d.Code == "TRP022");
        Assert.Contains(failure.Manifest!.Diagnostics, d => d.Code == "TRP022");
    }
}

public sealed class BinderOptionsDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<BinderOptionsComponent>(); }
public sealed class BinderOptionsComponent : IComponent
{ public void Compose(IComponentBuilder c) => c.Options<Owned>("Owned", (section, owned) => section.Bind(owned), required: false); }
public sealed class BinderSecondBindingDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<BinderSecondBindingComponent>(); }
public sealed class BinderSecondBindingComponent : IComponent
{
    public void Compose(IComponentBuilder c)
    {
        c.Options<Owned>("Owned", (section, owned) => section.Bind(owned), required: false);
        c.Services(s => s.AddOptions<Owned>().BindConfiguration("Owned"));
    }
}
public sealed class Owned { public List<string> Values { get; set; } = []; public string Label { get; set; } = ""; }
public sealed class OptionsDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<OptionsComponent>(); }
public sealed class OptionsComponent : IComponent { public void Compose(IComponentBuilder c) => c.Options<Owned>("Owned", required: false); }
public sealed class GeneratedOptionsDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<GeneratedOptionsComponent>(); }
public sealed class GeneratedOptionsComponent : IComponent { public void Compose(IComponentBuilder c) => c.Options<GeneratedOwned>("Owned", required: false); }
public sealed class SecondBindingDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<SecondBindingComponent>(); }
public sealed class SecondBindingComponent : IComponent
{
    public void Compose(IComponentBuilder c)
    {
        c.Options<Owned>("Owned", required: false);
        c.Services(s => s.AddOptions<Owned>().BindConfiguration("Owned"));
    }
}
public sealed class ManualReload : IOptionsChangeTokenSource<Owned>
{
    public string Name => Microsoft.Extensions.Options.Options.DefaultName;
    public Microsoft.Extensions.Primitives.IChangeToken GetChangeToken() => new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);
}
