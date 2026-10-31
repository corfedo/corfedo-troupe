using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe;

internal sealed class ContributorBuilder(CompositionOwner owner) : IRoleBuilder, IComponentBuilder
{
    internal CompositionOwner Owner { get; } = owner;
    internal readonly Dictionary<Type, Action<ContributorBuilder>> Requirements = [];
    internal readonly List<Action<CompositionSession, CompositionOwner>> Actions = [];
    internal readonly Dictionary<Type, object> Extensions = [];

    public IContributorBuilder Requires<T>() where T : IComponent, new()
    { Requirements.TryAdd(typeof(T), b => new T().Compose(b)); return this; }
    public void Services(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Actions.Add((session, owner) => session.Track(owner, configure));
    }
    public void Contribute<T>(Action<T> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Actions.Add((s, o) => s.Contributions.Add(new(o, typeof(T), target => configure((T)target))));
    }
    public void MultipleAllowed<T>() => Actions.Add((s, _) => s.Multiple.Add(typeof(T)));
    public void ExclusiveService<T>(object? key = null) => Actions.Add((s, _) => s.Exclusive.Add((typeof(T), key)));
}
