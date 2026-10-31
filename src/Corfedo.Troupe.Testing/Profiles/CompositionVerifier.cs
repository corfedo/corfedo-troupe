using Corfedo.Troupe.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe.Testing;

/// <summary>Builds real declared profiles without starting the host lifecycle.</summary>
public static class CompositionVerifier
{
    /// <summary>Composes, overrides, validates, builds, resolves declared options, maps web endpoints, and disposes each profile.</summary>
    /// <remarks>
    /// Supply a fresh HostApplicationBuilder or WebApplicationBuilder per profile. The supplied builder owns its provider factory and validation settings.
    /// Enable ValidateOnBuild and ValidateScopes when configuring the default provider.
    /// The factory may compose the same definition and apply its application adapters before returning the builder.
    /// Build, callbacks, mapping, options factories and validators can have side effects; use isolated configuration and inert overrides.
    /// Arbitrary services and factory bodies are not resolved for validation. No profiles means verification is rejected.
    /// </remarks>
    public static async Task<ProfileVerificationResult> VerifyProfilesAsync<T>(
        Func<ProfileIdentity, IHostApplicationBuilder> createBuilder,
        Action<ProfileIdentity, IServiceCollection>? overrides = null,
        CancellationToken cancellationToken = default) where T : ICompositionDefinition, new()
    {
        ArgumentNullException.ThrowIfNull(createBuilder);
        var definition = DefinitionBuilder.Create<T>();
        if (definition.Profiles.Count == 0) throw new InvalidOperationException("Profile verification requires declared serving profiles.");
        var entries = new List<ProfileVerificationEntry>();
        var failures = new List<ProfileVerificationFailure>();
        foreach (var profile in definition.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IHost? host = null;
            IHostApplicationBuilder? builder = null;
            CompositionSession? session = null;
            CompositionManifest? manifest = null;
            AspNetCompositionManifest? webManifest = null;
            try
            {
                builder = createBuilder(profile) ?? throw new InvalidOperationException("The builder factory returned null.");
                if (builder is not HostApplicationBuilder && builder is not WebApplicationBuilder)
                    throw new ArgumentException("Supply a HostApplicationBuilder or WebApplicationBuilder.", nameof(createBuilder));
                session = CompositionSession.TryGet(builder);
                if (session is not null)
                {
                    if (session.DefinitionType != typeof(T) || session.Runtime.Profile?.Name != profile.Name)
                        throw new InvalidOperationException("The supplied composition must use this definition and exact profile.");
                }
                else
                {
                    builder.Configuration["Profile"] = profile.Name;
                    builder.Compose<T>(); session = CompositionSession.Get(builder);
                }
                if (overrides is not null) session.ApplyTestOverrides(profile, services => overrides(profile, services));
                session.FinalizeComposition();
                host = builder switch
                {
                    WebApplicationBuilder web => web.Build(),
                    HostApplicationBuilder generic => generic.Build(),
                    _ => throw new InvalidOperationException("Unsupported host builder.")
                };
                cancellationToken.ThrowIfCancellationRequested();
                session.ResolveDeclaredOptions(host.Services, cancellationToken);
                if (host is WebApplication application) application.UseComposition();
                cancellationToken.ThrowIfCancellationRequested();
                manifest = session.ValidateServingComposition();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                if (session is null && builder is not null) session = CompositionSession.TryGet(builder);
                manifest = (exception as CompositionException)?.Manifest ?? session?.FailureSnapshot(exception);
                failures.Add(new(profile, manifest, exception, false));
            }
            finally
            {
                if (session is not null)
                {
                    // Snapshots do not invoke rules during cleanup; a failed rule must not prevent disposal.
                    manifest ??= session.FailureSnapshot();
                    webManifest = session.IntegrationSnapshot<AspNetCompositionManifest>();
                }
                if (host is not null)
                {
                    try
                    {
                        if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        else host.Dispose();
                    }
                    catch (Exception exception) { failures.Add(new(profile, manifest, exception, true)); }
                }
                entries.Add(new(profile, manifest, webManifest));
            }
        }
        return new(entries, failures);
    }
}
