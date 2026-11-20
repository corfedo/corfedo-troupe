using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe.Fixtures;

public sealed class GeneratedOwned { public List<string> Values { get; set; } = []; }

// Its owned binder is generated, as in a component library that enables the generator.
public sealed class GeneratedOptionsComponent : IComponent
{ public void Compose(IComponentBuilder c) => c.Options<GeneratedOwned>("Owned", (section, options) => section.Bind(options), required: false); }

// Built with the configuration binding source generator, which intercepts these calls and registers generated binders.
public static class GeneratedBinding
{
    public static IServiceCollection Add(string binding, IServiceCollection services, IConfiguration configuration, string section) => binding switch
    {
        "bind-configuration" => services.AddOptions<GeneratedOwned>().BindConfiguration(section).Services,
        "options-bind" => services.AddOptions<GeneratedOwned>().Bind(configuration.GetSection(section)).Services,
        "configure-section" => services.Configure<GeneratedOwned>(configuration.GetSection(section)),
        "lambda-bind" => services.Configure<GeneratedOwned>(o => configuration.GetSection(section).Bind(o)),
        "named-bind-configuration" => services.AddOptions<GeneratedOwned>("secondary").BindConfiguration(section).Services,
        "named-configure" => services.Configure<GeneratedOwned>("secondary", configuration.GetSection(section)),
        _ => throw new ArgumentException(binding, nameof(binding))
    };
}
