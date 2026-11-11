using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Corfedo.Troupe.Aspire.Hosting;

namespace Aspire.Hosting;

/// <summary>Runs Corfedo Troupe compositions as Aspire project resources.</summary>
public static class TroupeResourceBuilderExtensions
{
    /// <summary>
    /// Adds a project resource whose process composes a Troupe definition with <c>AddTroupe</c>, and marks it with
    /// <see cref="WithTroupe"/>. The resource gets its endpoints from the listeners the composition requests: the project's
    /// launch profile and the endpoints Aspire derives from its Kestrel configuration are left out, because Kestrel serves
    /// only those listeners.
    /// </summary>
    /// <remarks>
    /// Environment variables and arguments of the project's launch profile are not applied either; set what the process
    /// needs with <c>WithEnvironment</c> and <c>WithArgs</c>. A launch profile usually sets the environment name, so in run
    /// mode the resource gets the AppHost's environment name as <c>DOTNET_ENVIRONMENT</c> unless the AppHost sets one.
    /// </remarks>
    public static IResourceBuilder<ProjectResource> AddTroupeProject<TProject>(this IDistributedApplicationBuilder builder, [ResourceName] string name)
        where TProject : IProjectMetadata, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddProject<TProject>(name, options =>
        {
            options.ExcludeLaunchProfile = true;
            options.ExcludeKestrelEndpoints = true;
        }).WithEnvironment(context =>
        {
            if (context.ExecutionContext.IsRunMode && !context.EnvironmentVariables.ContainsKey("ASPNETCORE_ENVIRONMENT"))
                context.EnvironmentVariables.TryAdd("DOTNET_ENVIRONMENT", builder.Environment.EnvironmentName);
        }).WithTroupe();
    }

    /// <summary>
    /// Marks a project resource whose process composes a Troupe definition with <c>AddTroupe</c>. Before any resource starts,
    /// the project describes the selection it makes with this resource's arguments and environment; each listener it requests
    /// gets an HTTP endpoint (unless the AppHost declares one with that name) and a <c>Kestrel__Endpoints__&lt;name&gt;__Url</c>
    /// on the endpoint's target port. The errors <c>AddTroupe</c> reports for every marked resource, and HTTP endpoints of a
    /// marked resource that no listener serves, fail the AppHost before any resource starts.
    /// </summary>
    /// <remarks>
    /// The description runs the built project with <c>dotnet run --no-build</c> and the AppHost's build configuration. Literal
    /// argument and environment values pass unchanged; endpoint references, expressions and other values that are known only
    /// at run time are replaced by <c>http://troupe-describe.invalid</c> and count as present. The resource's argument and
    /// environment callbacks run for the description, before anything is allocated, and again when Aspire starts the
    /// resource; a callback that reads an allocated value, such as <c>EndpointReference.Url</c>, fails the AppHost with that
    /// reason. Checks that run after <c>AddTroupe</c>, when the host is built, are reported by the started process.
    /// Prefer <see cref="AddTroupeProject{TProject}"/> over <c>AddProject</c>: a project added with its launch profile or
    /// Kestrel configuration has endpoints that no listener serves.
    /// </remarks>
    public static IResourceBuilder<ProjectResource> WithTroupe(this IResourceBuilder<ProjectResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Resource.Annotations.OfType<TroupeAnnotation>().Any()) return builder;
        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<TroupeEventingSubscriber>();
        return builder.WithAnnotation(new TroupeAnnotation(builder));
    }

    /// <summary>Selects a declared profile through the <c>Profile</c> environment variable and marks the resource with <see cref="WithTroupe"/>.</summary>
    public static IResourceBuilder<ProjectResource> WithTroupeProfile(this IResourceBuilder<ProjectResource> builder, string profile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        return builder.WithEnvironment("Profile", profile).WithTroupe();
    }

    /// <summary>Selects roles through the <c>Roles</c> environment variable and marks the resource with <see cref="WithTroupe"/>.</summary>
    public static IResourceBuilder<ProjectResource> WithTroupeRoles(this IResourceBuilder<ProjectResource> builder, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.Length == 0 || roles.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Select at least one role, without empty names.", nameof(roles));
        return builder.WithEnvironment("Roles", string.Join(",", roles)).WithTroupe();
    }
}
