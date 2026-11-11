using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe;

internal static class OwnedOptions
{
    internal static void Register<T>(CompositionSession session, CompositionOwner owner, string section, bool required) where T : class, new()
    {
        session.RegisterOptions(typeof(T), sp => _ = sp.GetRequiredService<IOptions<T>>().Value);
        session.RegisterBinding(owner, typeof(T), section, (sp, trace) => Bindings<T>(sp, trace, section));
        session.ConfigureServices(owner, services =>
        {
            services.AddOptions<T>().BindConfiguration(section).ValidateOnStart();
            if (required)
                services.AddSingleton<IValidateOptions<T>>(new RequiredSection<T>(session.Configuration, owner, section));
        });
    }
    // Runs each configure and post-configure action of the default instance on a scratch instance and counts those that enumerate
    // the section or a subsection. Every binder, reflection or source-generated, enumerates a section to add collection entries,
    // whichever API registered it; reading single keys, other sections or named instances does not count.
    private static int Bindings<T>(IServiceProvider services, BindingTrace trace, string section) where T : class, new()
    {
        var found = services.GetServices<IConfigureOptions<T>>().Count(configure => trace.Enumerates(section, () =>
        {
            if (configure is IConfigureNamedOptions<T> named) named.Configure(Microsoft.Extensions.Options.Options.DefaultName, new T());
            else configure.Configure(new T());
        }));
        return found + services.GetServices<IPostConfigureOptions<T>>()
            .Count(post => trace.Enumerates(section, () => post.PostConfigure(Microsoft.Extensions.Options.Options.DefaultName, new T())));
    }
    private sealed class RequiredSection<T>(IConfiguration configuration, CompositionOwner owner, string section) : IValidateOptions<T> where T : class
    {
        public ValidateOptionsResult Validate(string? name, T options) =>
            configuration.GetSection(section).Exists() ? ValidateOptionsResult.Success :
                ValidateOptionsResult.Fail($"{owner.Name}: required section {section} is missing.");
    }
}
