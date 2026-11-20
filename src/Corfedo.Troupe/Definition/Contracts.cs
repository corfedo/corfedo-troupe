using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe;

/// <summary>Declares one selectable workload. Declarations must not acquire resources or perform I/O.</summary>
public interface IRole
{
    /// <summary>Gets the stable, ordinal selection name: nonempty, trimmed and without commas.</summary>
    static abstract string Name { get; }
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
    /// <summary>Requires a component in every composition of this definition; without roles, these requirements are the application.</summary>
    ICompositionDefinitionBuilder Requires<T>() where T : IComponent, new();
    /// <summary>Registers a stateless role for selection by Roles configuration; profiles register the roles they name.</summary>
    ICompositionDefinitionBuilder Role<T>() where T : IRole, new();
    /// <summary>Declares an exact supported role set and registers its roles. Declared profiles are always strict.</summary>
    ICompositionDefinitionBuilder Profile(string name, Action<IProfileBuilder> roles);
    /// <summary>Adds an application diagnostic rule.</summary>
    ICompositionDefinitionBuilder Rule(ICompositionRule rule);
}

/// <summary>Names the roles of one serving profile.</summary>
public interface IProfileBuilder
{
    /// <summary>Adds a role, named by its <see cref="IRole.Name"/>, to the profile.</summary>
    IProfileBuilder Role<T>() where T : IRole, new();
}

/// <summary>Records contributions. Callbacks execute only after the selected graph is valid and ordered.</summary>
public interface IContributorBuilder
{
    /// <summary>Requires a shared component before this contributor.</summary>
    IContributorBuilder Requires<T>() where T : IComponent, new();
    /// <summary>Records a synchronous service callback. Do not retain the supplied collection.</summary>
    void Services(Action<IServiceCollection> configure);
    /// <summary>Records a synchronous callback for library extensions of the host builder, owned like <see cref="Services"/>.</summary>
    /// <remarks>
    /// Configuration and the environment are read-only. These fail with TRP023: configuration value writes,
    /// <c>Configuration.Build</c>, <c>Configuration.Sources</c>, <c>Configuration.Properties</c>, environment setters,
    /// <c>ConfigureContainer</c>, and use of the builder after the callback returns.
    /// </remarks>
    void HostBuilder(Action<IHostApplicationBuilder> configure);
    /// <summary>Binds one owned section to default options, with required presence validation and ValidateOnStart.</summary>
    /// <remarks>Binds by reflection. Trimmed and Native AOT applications use the overload that takes a binder.</remarks>
    [RequiresUnreferencedCode(OwnedOptions.ReflectionBinding)]
    [RequiresDynamicCode(OwnedOptions.ReflectionBinding)]
    void Options<T>(string section, bool required = true) where T : class, new();
    /// <summary>Binds owned options and explicitly registers a DI-constructed validator.</summary>
    /// <remarks>Binds by reflection. Trimmed and Native AOT applications use the overload that takes a binder.</remarks>
    [RequiresUnreferencedCode(OwnedOptions.ReflectionBinding)]
    [RequiresDynamicCode(OwnedOptions.ReflectionBinding)]
    void Options<T, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TValidator>(string section, bool required = true)
        where T : class, new() where TValidator : class, IValidateOptions<T>;
    /// <summary>
    /// Binds one owned section to default options with <paramref name="bind"/>, which receives the section, with required
    /// presence validation and ValidateOnStart.
    /// </summary>
    /// <remarks>
    /// A binder such as <c>(section, options) =&gt; section.Bind(options)</c> names the options type at the call site, so the
    /// configuration binding source generator replaces it in trimmed and Native AOT applications.
    /// </remarks>
    void Options<T>(string section, Action<IConfiguration, T> bind, bool required = true) where T : class, new();
    /// <summary>Binds owned options with <paramref name="bind"/> and explicitly registers a DI-constructed validator.</summary>
    /// <remarks>See <see cref="Options{T}(string, Action{IConfiguration, T}, bool)"/>.</remarks>
    void Options<T, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TValidator>(string section, Action<IConfiguration, T> bind, bool required = true)
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
    /// <summary>Gets the selected profile, or null when the definition declares no profiles or a test root replaces the selection.</summary>
    ProfileIdentity? Profile { get; }
    /// <summary>Gets selected roles in ordinal name order; empty when a test root replaces the selection.</summary>
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
