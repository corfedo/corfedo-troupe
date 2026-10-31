using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe;

internal sealed class ContributorBuilder(CompositionOwner owner) : IRoleBuilder, IComponentBuilder
{
    internal CompositionOwner Owner { get; } = owner;
    internal readonly Dictionary<Type, Action<ContributorBuilder>> Requirements = [];
    internal readonly List<Action<CompositionSession, CompositionOwner>> Actions = [];

    public IContributorBuilder Requires<T>() where T : IComponent, new()
    { Requirements.TryAdd(typeof(T), b => new T().Compose(b)); return this; }
    public void Services(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Actions.Add((session, owner) => session.Track(owner, configure));
    }
    public void Options<T>(string section, bool required = true) where T : class, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        Actions.Add((s, o) => OwnedOptions.Register<T>(s, o, section, required));
    }
    public void Options<T, TValidator>(string section, bool required = true)
        where T : class, new() where TValidator : class, IValidateOptions<T>
    {
        Options<T>(section, required);
        Services(s => s.AddSingleton<IValidateOptions<T>, TValidator>());
    }
    public void ReadsConfiguration(string section, bool required = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        Actions.Add((s, o) => s.Configuration.Add(new(o, section, required, null)));
    }
    public void Check(Func<IServiceProvider, CancellationToken, Task> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        Actions.Add((s, o) => s.Checks.Add(new(o, check)));
    }
    public void Contribute<T>(Action<T> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Actions.Add((s, o) => s.Contributions.Add(new(o, typeof(T), target => configure((T)target))));
    }
    public void MultipleAllowed<T>() => Actions.Add((s, _) => s.Multiple.Add(typeof(T)));
    public void ExclusiveService<T>(object? key = null) => Actions.Add((s, _) => s.Exclusive.Add((typeof(T), key)));
}
