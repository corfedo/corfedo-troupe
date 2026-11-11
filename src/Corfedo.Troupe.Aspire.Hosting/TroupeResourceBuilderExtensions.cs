using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Corfedo.Troupe.Aspire.Hosting;

namespace Aspire.Hosting;

/// <summary>Runs Corfedo Troupe compositions as Aspire project resources.</summary>
public static class TroupeResourceBuilderExtensions
{
    /// <summary>
    /// Marks a project resource whose process composes a Troupe definition with <c>AddTroupe</c>. Before any resource starts,
    /// the project describes the selection it makes with this resource's arguments and environment; each listener it requests
    /// gets an HTTP endpoint (unless the AppHost declares one with that name) and a <c>Kestrel__Endpoints__&lt;name&gt;__Url</c>
    /// on the endpoint's target port. Composition errors of every marked resource fail the AppHost before any resource starts.
    /// </summary>
    /// <remarks>
    /// The description runs the built project with <c>dotnet run --no-build</c> and the AppHost's build configuration. Literal
    /// argument and environment values pass unchanged; endpoint references, expressions and other values that are known only
    /// at run time are replaced by <c>http://troupe-describe.invalid</c> and count as present.
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
