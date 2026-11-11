using System.Collections.Immutable;
using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;

namespace Corfedo.Troupe.Aspire.Hosting;

// Before any resource starts (run mode) or the manifest is written (publish mode): describes every Troupe resource, binds
// their listeners, publishes dashboard properties and fails once with every resource's problems.
internal sealed class TroupeEventingSubscriber(ResourceNotificationService notifications, ILogger<TroupeEventingSubscriber> logger)
    : IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext execution, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>((e, token) => OnBeforeStartAsync(e, execution, token));
        return Task.CompletedTask;
    }

    private async Task OnBeforeStartAsync(BeforeStartEvent e, DistributedApplicationExecutionContext execution, CancellationToken cancellationToken)
    {
        var marked = e.Model.Resources.OfType<ProjectResource>()
            .Select(r => (Resource: r, Annotation: r.Annotations.OfType<TroupeAnnotation>().FirstOrDefault()))
            .Where(m => m.Annotation is not null).ToArray();
        if (marked.Length == 0) return;
        var results = await Task.WhenAll(marked.Select(async m =>
        {
            var configuration = m.Annotation!.Builder.ApplicationBuilder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
            var result = await ProjectDescriber.DescribeAsync(m.Resource, execution, configuration, cancellationToken).ConfigureAwait(false);
            return new DescribedResource(m.Resource, m.Annotation.Builder, result.Description, result.Failure);
        })).ConfigureAwait(false);

        foreach (var described in results.Where(r => r.Description is not null)) Bind(described, execution.IsPublishMode);
        if (ProblemReport.Create(results) is { } report) throw new DistributedApplicationException(report);
        if (execution.IsRunMode)
            foreach (var described in results)
                await notifications.PublishUpdateAsync(described.Resource, snapshot => snapshot with
                {
                    Properties = snapshot.Properties.AddRange(Properties(described, results))
                }).ConfigureAwait(false);
        logger.LogDebug("Described {Count} Troupe resources.", results.Length);
    }

    // Each listener gets an HTTP endpoint unless the AppHost declared one with its name, and Kestrel binds the endpoint's target port.
    // Kestrel ignores ASPNETCORE_URLS when named endpoints are configured and logs a warning for it, so Aspire leaves the
    // listeners' endpoints out of that variable.
    private static void Bind(DescribedResource described, bool publishing)
    {
        var host = publishing ? "*" : "localhost";
        var listeners = described.Description!.Listeners.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (listeners.Count > 0) described.Builder.WithEndpointsInEnvironment(endpoint => !listeners.Contains(endpoint.Name));
        foreach (var listener in described.Description.Listeners)
        {
            var declared = described.Resource.Annotations.OfType<EndpointAnnotation>()
                .FirstOrDefault(a => string.Equals(a.Name, listener.Name, StringComparison.OrdinalIgnoreCase));
            if (declared is null) described.Builder.WithHttpEndpoint(name: listener.Name);
            var endpoint = described.Builder.GetEndpoint(declared?.Name ?? listener.Name);
            var scheme = declared?.UriScheme == "https" ? "https" : "http";
            described.Builder.WithEnvironment($"Kestrel__Endpoints__{listener.Name}__Url",
                ReferenceExpression.Create($"{scheme}://{host}:{endpoint.Property(EndpointProperty.TargetPort)}"));
        }
    }

    private static IEnumerable<ResourcePropertySnapshot> Properties(DescribedResource described, IReadOnlyList<DescribedResource> all)
    {
        if (described.Description is not { Selection: { } selection } description) yield break;
        if (selection.Profile is { } profile) yield return new("troupe.profile", profile);
        yield return new("troupe.roles", string.Join(", ", selection.Roles));
        yield return new("troupe.listeners", string.Join(", ", description.Listeners.Select(l => l.Name)));
        var project = described.Resource.GetProjectMetadata().ProjectPath;
        var served = all.Where(r => r.Resource.GetProjectMetadata().ProjectPath == project)
            .SelectMany(r => r.Description?.Selection?.Roles ?? []).ToHashSet(StringComparer.Ordinal);
        yield return new("troupe.unservedRoles", string.Join(", ", description.Roles.Where(r => !served.Contains(r))));
    }
}

internal sealed record DescribedResource(ProjectResource Resource, IResourceBuilder<ProjectResource> Builder, Description? Description, string? Failure);
