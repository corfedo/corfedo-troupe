using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe;

internal sealed class ContributorBuilder(CompositionOwner owner) : IRoleBuilder, IComponentBuilder
{
    private readonly Dictionary<Type, Action<ContributorBuilder>> requirements = [];
    private readonly List<Action<CompositionSession, CompositionOwner>> actions = [];
    private readonly List<ConfigurationRead> configuration = [];
    private bool closed;
    internal CompositionOwner Owner { get; } = owner;
    internal IReadOnlyDictionary<Type, Action<ContributorBuilder>> Requirements => requirements;
    internal IReadOnlyList<ConfigurationRead> Configuration => configuration;
    internal Func<CompositionSession, ICompositionIntegration>? IntegrationFactory { get; private set; }

    internal void Close() => closed = true;
    private void EnsureOpen()
    {
        if (closed) throw Errors.Fail(DiagnosticCodes.ClosedDeclaration,
            $"Declarations for {Owner.Name} are closed; record contributions only inside Compose.", Owner);
    }
    internal void Record(Action<CompositionSession, CompositionOwner> action)
    { EnsureOpen(); actions.Add(action); }
    internal void UseIntegration(Func<CompositionSession, ICompositionIntegration> factory)
    { EnsureOpen(); IntegrationFactory ??= factory; }
    internal Action<CompositionSession, CompositionOwner>[] TakeActions()
    { var recorded = actions.ToArray(); actions.Clear(); return recorded; }
    internal void Release()
    { actions.Clear(); requirements.Clear(); configuration.Clear(); IntegrationFactory = null; }

    public IContributorBuilder Requires<T>() where T : IComponent, new()
    { EnsureOpen(); requirements.TryAdd(typeof(T), b => new T().Compose(b)); return this; }
    public void Services(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Record((s, o) => s.ConfigureServices(o, configure));
    }
    public void Options<T>(string section, bool required = true) where T : class, new()
    {
        EnsureOpen(); ArgumentException.ThrowIfNullOrWhiteSpace(section);
        configuration.Add(new(Owner, section, required, typeof(T)));
        Record((s, o) => OwnedOptions.Register<T>(s, o, section, required));
    }
    public void Options<T, TValidator>(string section, bool required = true)
        where T : class, new() where TValidator : class, IValidateOptions<T>
    {
        Options<T>(section, required);
        Services(s => s.AddSingleton<IValidateOptions<T>, TValidator>());
    }
    public void ReadsConfiguration(string section, bool required = true)
    {
        EnsureOpen(); ArgumentException.ThrowIfNullOrWhiteSpace(section);
        configuration.Add(new(Owner, section, required, null));
    }
    public void ReadsConfigurationUnless<TComponent>(string section) where TComponent : IComponent
    {
        EnsureOpen(); ArgumentException.ThrowIfNullOrWhiteSpace(section);
        configuration.Add(new(Owner, section, false, null, typeof(TComponent)));
    }
    public void Check(Func<IServiceProvider, CancellationToken, Task> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        Record((s, o) => s.AddCheck(o, check));
    }
    public void Contribute<T>(Action<T> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Record((s, o) => s.AddContribution(o, typeof(T), target => configure((T)target)));
    }
    public void MultipleAllowed<T>() => Record((s, _) => s.AllowMultiple(typeof(T)));
    public void ExclusiveService<T>(object? key = null) => Record((s, _) => s.RequireExclusive(typeof(T), key));
}
