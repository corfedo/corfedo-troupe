using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe;

/// <summary>Assigns the stable, ordinal selection name of a role.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RoleAttribute(string name) : Attribute
{
    /// <summary>Gets the selection name.</summary>
    public string Name { get; } = name;
}

/// <summary>Declares one selectable workload. Declarations must not acquire resources or perform I/O.</summary>
public interface IRole
{
    /// <summary>Records dependencies and deferred callbacks for this role.</summary>
    void Compose(IRoleBuilder role);
}

/// <summary>Declares a shared dependency, included once in the selected closure.</summary>
public interface IComponent
{
    /// <summary>Records dependencies and deferred callbacks without acquiring resources or performing I/O.</summary>
    void Compose(IComponentBuilder component);
}

/// <summary>Defines an application's known roles and supported serving profiles.</summary>
public interface ICompositionDefinition
{
    /// <summary>Records the definition.</summary>
    void Define(ICompositionDefinitionBuilder definition);
}

/// <summary>Records roles, exact profiles and application composition rules.</summary>
public interface ICompositionDefinitionBuilder
{
    /// <summary>Registers a stateless role definition with an explicit unique name.</summary>
    ICompositionDefinitionBuilder Role<T>() where T : IRole, new();
    /// <summary>Declares an exact supported role set. Declared profiles are always strict.</summary>
    ICompositionDefinitionBuilder Profile(string name, params string[] roles);
    /// <summary>Adds an application diagnostic rule.</summary>
    ICompositionDefinitionBuilder Rule(ICompositionRule rule);
}

/// <summary>Records contributions. Callbacks execute only after the selected graph is valid and ordered.</summary>
public interface IContributorBuilder
{
    /// <summary>Requires a shared component before this contributor.</summary>
    IContributorBuilder Requires<T>() where T : IComponent, new();
    /// <summary>Records a synchronous service callback. Do not retain the supplied collection.</summary>
    void Services(Action<IServiceCollection> configure);
    /// <summary>Binds one owned section to default options, with required presence validation and ValidateOnStart.</summary>
    void Options<T>(string section, bool required = true) where T : class, new();
    /// <summary>Binds owned options and explicitly registers a DI-constructed validator.</summary>
    void Options<T, TValidator>(string section, bool required = true)
        where T : class, new() where TValidator : class, IValidateOptions<T>;
    /// <summary>Records a manual configuration read and enforces required section presence.</summary>
    void ReadsConfiguration(string section, bool required = true);
    /// <summary>Records a manual configuration read that is required only when the component is not composed in this process.</summary>
    void ReadsConfigurationUnless<TComponent>(string section) where TComponent : IComponent;
    /// <summary>Records a sequential check in Troupe's StartingAsync, after hosted construction and startup options validation.</summary>
    void Check(Func<IServiceProvider, CancellationToken, Task> check);
    /// <summary>Records a callback for an application-owned third-party builder.</summary>
    void Contribute<T>(Action<T> configure);
    /// <summary>Suppresses the advisory duplicate-service diagnostic for this service type.</summary>
    void MultipleAllowed<T>();
    /// <summary>Requires at most one active registration in this type and key slot.</summary>
    void ExclusiveService<T>(object? key = null);
}

/// <summary>Records a role's declarations without exposing the selected roles.</summary>
public interface IRoleBuilder : IContributorBuilder;
/// <summary>Records a component's declarations without exposing the selected roles.</summary>
public interface IComponentBuilder : IContributorBuilder;

/// <summary>Provides runtime membership and immutable manifest snapshots.</summary>
public interface IComposition
{
    /// <summary>Tests whether the role belongs to this process.</summary>
    bool Contains<TRole>() where TRole : IRole;
    /// <summary>Tests whether the component is composed in this process.</summary>
    bool Includes<TComponent>() where TComponent : IComponent;
    /// <summary>Gets the selected profile, or null when the definition declares no profiles.</summary>
    ProfileIdentity? Profile { get; }
    /// <summary>Gets selected roles in ordinal name order.</summary>
    IReadOnlyList<RoleIdentity> Roles { get; }
    /// <summary>Gets the last successfully validated immutable snapshot, including completed adapter and test boundaries.</summary>
    CompositionManifest Manifest { get; }
}

/// <summary>Evaluates application policy without executing service factories.</summary>
public interface ICompositionRule
{
    /// <summary>Returns diagnostics for the supplied immutable snapshot.</summary>
    IEnumerable<CompositionDiagnostic> Evaluate(CompositionManifest manifest);
}
