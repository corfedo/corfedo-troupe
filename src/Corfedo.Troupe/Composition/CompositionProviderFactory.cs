using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe;

// Build calls CreateBuilder after every adapter has registered, so finalization captures their registrations there.
internal sealed class CompositionProviderFactory(CompositionSession session) : IServiceProviderFactory<IServiceCollection>
{
    public IServiceCollection CreateBuilder(IServiceCollection services)
    {
        session.FinalizeComposition();
        return services;
    }

    public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder) =>
        containerBuilder.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}
