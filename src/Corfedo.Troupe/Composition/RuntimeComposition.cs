namespace Corfedo.Troupe;

internal sealed class RuntimeComposition(IReadOnlyList<RoleIdentity> roles) : IComposition
{
    private PublishedComposition? published;
    public IReadOnlyList<RoleIdentity> Roles { get; } = roles;
    public bool Contains<TRole>() where TRole : IRole => Roles.Any(r => r.Type == typeof(TRole));
    public CompositionManifest Manifest => Volatile.Read(ref published)?.Manifest ??
        throw new InvalidOperationException("The composition has no validated snapshot.");
    internal void Publish(CompositionManifest manifest, object? integrationSnapshot) =>
        Volatile.Write(ref published, new(manifest, integrationSnapshot));
    internal T? IntegrationSnapshot<T>() where T : class => Volatile.Read(ref published)?.IntegrationSnapshot as T;
    private sealed record PublishedComposition(CompositionManifest Manifest, object? IntegrationSnapshot);
}
