using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Corfedo.Troupe;

// The host builder a HostBuilder callback receives: services, logging and metrics registrations go through the owner's tracked
// collection; configuration and the environment are readable but cannot change, since AddTroupe has already checked the declared
// reads. Configuration sources and builder properties stay hidden: a source can hold a writable configuration
// (ChainedConfigurationSource), and a property write reloads every source.
internal sealed class GuardedHostBuilder(IHostApplicationBuilder host, IServiceCollection services, CompositionOwner owner) : IHostApplicationBuilder
{
    private bool closed;
    internal void Close() => closed = true;
    private T Open<T>(T value) => closed ? throw Guard("the host builder callback has completed; do not retain the builder") : value;
    internal CompositionException Guard(string reason) => Errors.Fail("TRP023", $"{owner.Name}: {reason}.", owner);

    public IConfigurationManager Configuration => Open(new GuardedConfiguration(this, host.Configuration));
    public IHostEnvironment Environment => Open(new GuardedEnvironment(this, host.Environment));
    public ILoggingBuilder Logging => Open(new LoggingBuilder(services));
    public IMetricsBuilder Metrics => Open(new MetricsBuilder(services));
    public IDictionary<object, object> Properties => Open(host.Properties);
    public IServiceCollection Services => Open(services);
    public void ConfigureContainer<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory, Action<TContainerBuilder>? configure = null)
        where TContainerBuilder : notnull => throw Guard("ConfigureContainer would replace the service provider factory that AddTroupe installs");

    private sealed class LoggingBuilder(IServiceCollection services) : ILoggingBuilder { public IServiceCollection Services => services; }
    private sealed class MetricsBuilder(IServiceCollection services) : IMetricsBuilder { public IServiceCollection Services => services; }

    private sealed class GuardedEnvironment(GuardedHostBuilder owner, IHostEnvironment inner) : IHostEnvironment
    {
        private CompositionException Rejected() => owner.Guard("the host environment cannot change after AddTroupe composed the host");
        public string EnvironmentName { get => inner.EnvironmentName; set => throw Rejected(); }
        public string ApplicationName { get => inner.ApplicationName; set => throw Rejected(); }
        public string ContentRootPath { get => inner.ContentRootPath; set => throw Rejected(); }
        public IFileProvider ContentRootFileProvider { get => inner.ContentRootFileProvider; set => throw Rejected(); }
    }

    private sealed class GuardedConfiguration(GuardedHostBuilder owner, IConfigurationManager inner) : IConfigurationManager
    {
        public string? this[string key] { get => inner[key]; set => throw owner.Guard("configuration values cannot change after AddTroupe checked the declared reads"); }
        public IConfigurationSection GetSection(string key) => new GuardedSection(owner, inner.GetSection(key));
        public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren().Select(c => new GuardedSection(owner, c));
        public IChangeToken GetReloadToken() => inner.GetReloadToken();
        public IDictionary<string, object> Properties => throw Rejected();
        public IList<IConfigurationSource> Sources => throw Rejected();
        public IConfigurationBuilder Add(IConfigurationSource source) => throw Rejected();
        // ConfigurationManager.Build returns the writable manager itself, and a root exposes providers that accept Set.
        public IConfigurationRoot Build() => throw owner.Guard("Configuration.Build would expose the writable configuration root; read values through Configuration");
        internal CompositionException Rejected() => owner.Guard("configuration sources and their properties belong before AddTroupe, which checks declared configuration reads");

        private sealed class GuardedSection(GuardedHostBuilder owner, IConfigurationSection inner) : IConfigurationSection
        {
            private CompositionException Rejected() => owner.Guard("configuration values cannot change after AddTroupe checked the declared reads");
            public string? this[string key] { get => inner[key]; set => throw Rejected(); }
            public string Key => inner.Key;
            public string Path => inner.Path;
            public string? Value { get => inner.Value; set => throw Rejected(); }
            public IConfigurationSection GetSection(string key) => new GuardedSection(owner, inner.GetSection(key));
            public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren().Select(c => new GuardedSection(owner, c));
            public IChangeToken GetReloadToken() => inner.GetReloadToken();
        }
    }
}
