using Aspire.Hosting.ApplicationModel;

namespace Corfedo.Troupe.Aspire.Hosting;

// Marks a project resource that composes a Troupe definition; the builder adds endpoints and environment after describing.
internal sealed class TroupeAnnotation(IResourceBuilder<ProjectResource> builder) : IResourceAnnotation
{
    internal IResourceBuilder<ProjectResource> Builder { get; } = builder;
}
