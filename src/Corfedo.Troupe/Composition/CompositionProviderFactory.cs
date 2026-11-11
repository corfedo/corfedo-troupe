using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe;

// Build calls CreateBuilder after every adapter has registered, so finalization captures their registrations there; the built
// provider shows the options bindings that factories register.
internal sealed class CompositionProviderFactory(CompositionSession session) : IServiceProviderFactory<IServiceCollection>
{
    public IServiceCollection CreateBuilder(IServiceCollection services)
    {
        session.FinalizeComposition();
        return services;
    }

    public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder)
    {
        var provider = containerBuilder.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        try { session.CheckBindings(provider); }
        catch { provider.Dispose(); throw; }
        return provider;
    }
}
