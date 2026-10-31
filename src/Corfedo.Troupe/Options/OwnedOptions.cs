using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe;

internal static class OwnedOptions
{
    internal static void Register<T>(CompositionSession session, CompositionOwner owner, string section, bool required) where T : class, new()
    {
        if (session.ResolveOptions.ContainsKey(typeof(T)))
            throw Errors.Fail("TRP007", $"Options {typeof(T).FullName} already has an owned section; a second declaration of {section} is not supported.", owner);
        session.Configuration.Add(new(owner, section, required, typeof(T)));
        session.ResolveOptions.Add(typeof(T), sp => _ = sp.GetRequiredService<IOptions<T>>().Value);
        session.Track(owner, services =>
        {
            services.AddOptions<T>().BindConfiguration(section).ValidateOnStart();
            if (required)
                services.AddSingleton<IValidateOptions<T>>(new RequiredSection<T>(session.Builder.Configuration, owner, section));
        });
    }
    private sealed class RequiredSection<T>(IConfiguration configuration, CompositionOwner owner, string section) : IValidateOptions<T> where T : class
    {
        public ValidateOptionsResult Validate(string? name, T options) =>
            configuration.GetSection(section).Exists() ? ValidateOptionsResult.Success :
                ValidateOptionsResult.Fail($"{owner.Name}: required section {section} is missing.");
    }
}
