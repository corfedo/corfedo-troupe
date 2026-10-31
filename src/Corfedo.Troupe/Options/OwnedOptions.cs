using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe;

internal static class OwnedOptions
{
    internal static void Register<T>(CompositionSession session, CompositionOwner owner, string section, bool required) where T : class, new()
    {
        session.RegisterOptions(typeof(T), sp => _ = sp.GetRequiredService<IOptions<T>>().Value);
        session.ConfigureServices(owner, services =>
        {
            services.AddOptions<T>().BindConfiguration(section).ValidateOnStart();
            if (required)
                services.AddSingleton<IValidateOptions<T>>(new RequiredSection<T>(session.Configuration, owner, section));
        });
    }
    private sealed class RequiredSection<T>(IConfiguration configuration, CompositionOwner owner, string section) : IValidateOptions<T> where T : class
    {
        public ValidateOptionsResult Validate(string? name, T options) =>
            configuration.GetSection(section).Exists() ? ValidateOptionsResult.Success :
                ValidateOptionsResult.Fail($"{owner.Name}: required section {section} is missing.");
    }
}
