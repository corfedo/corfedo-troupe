using Corfedo.Troupe.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe.Testing;

/// <summary>Builds real compositions without starting the host lifecycle.</summary>
public static class CompositionVerifier
{
    /// <summary>Composes, overrides, validates, builds, resolves declared options, maps web endpoints, and disposes the composition the builder selects.</summary>
    /// <remarks>
    /// Use it for a definition without roles, or a builder whose configuration selects the roles. The factory returns a
    /// HostApplicationBuilder or WebApplicationBuilder; it may call AddTroupe with the same definition and apply its application
    /// adapters before returning the builder, otherwise the verifier composes it. The result's entry has a null profile unless the
    /// configuration selects one. Build, callbacks, mapping, options factories and validators can have side effects; use isolated
    /// configuration and inert overrides. Arbitrary services and factory bodies are not resolved for validation.
    /// </remarks>
    public static async Task<ProfileVerificationResult> VerifyAsync<T>(
        Func<IHostApplicationBuilder> createBuilder,
        Action<IServiceCollection>? overrides = null,
        CancellationToken cancellationToken = default) where T : ICompositionDefinition, new()
    {
        ArgumentNullException.ThrowIfNull(createBuilder);
        var entries = new List<ProfileVerificationEntry>();
        var failures = new List<ProfileVerificationFailure>();
        await VerifyOneAsync<T>(null, createBuilder, overrides, entries, failures, cancellationToken).ConfigureAwait(false);
        return new(entries, failures);
    }

    /// <summary>Composes, overrides, validates, builds, resolves declared options, maps web endpoints, and disposes each declared profile.</summary>
    /// <remarks>
    /// Supply a fresh HostApplicationBuilder or WebApplicationBuilder per profile. The factory may call AddTroupe with the same
    /// definition and apply its application adapters before returning the builder; otherwise the verifier composes it.
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
        if (definition.Profiles.Count == 0) throw new InvalidOperationException("Profile verification requires declared serving profiles; use VerifyAsync.");
        var entries = new List<ProfileVerificationEntry>();
        var failures = new List<ProfileVerificationFailure>();
        foreach (var profile in definition.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await VerifyOneAsync<T>(profile, () => createBuilder(profile),
                overrides is null ? null : services => overrides(profile, services), entries, failures, cancellationToken).ConfigureAwait(false);
        }
        return new(entries, failures);
    }

    // A null profile verifies whatever the builder's configuration selects; a declared profile is set and checked.
    private static async Task VerifyOneAsync<T>(ProfileIdentity? profile, Func<IHostApplicationBuilder> createBuilder,
        Action<IServiceCollection>? overrides, List<ProfileVerificationEntry> entries, List<ProfileVerificationFailure> failures,
        CancellationToken cancellationToken) where T : ICompositionDefinition, new()
    {
        IHost? host = null;
        IHostApplicationBuilder? builder = null;
        CompositionSession? session = null;
        CompositionManifest? manifest = null;
        AspNetCompositionManifest? webManifest = null;
        try
        {
            builder = createBuilder() ?? throw new InvalidOperationException("The builder factory returned null.");
            if (builder is not HostApplicationBuilder && builder is not WebApplicationBuilder)
                throw new ArgumentException("Supply a HostApplicationBuilder or WebApplicationBuilder.", nameof(createBuilder));
            session = CompositionSession.TryGet(builder);
            if (session is not null)
            {
                if (session.DefinitionType != typeof(T) || profile is not null && session.Runtime.Profile?.Name != profile.Name)
                    throw new InvalidOperationException("The supplied composition must use this definition and exact profile.");
            }
            else
            {
                if (profile is not null) builder.Configuration["Profile"] = profile.Name;
                builder.AddTroupe<T>(); session = CompositionSession.Get(builder);
            }
            if (overrides is not null) session.ApplyTestOverrides(profile?.Name ?? "verifier", overrides, privileged: true);
            host = builder switch
            {
                WebApplicationBuilder web => web.Build(),
                HostApplicationBuilder generic => generic.Build(),
                _ => throw new InvalidOperationException("Unsupported host builder.")
            };
            session.RequireFinalized();
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
            failures.Add(new(profile ?? session?.Runtime.Profile, manifest, exception, false));
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
                catch (Exception exception) { failures.Add(new(profile ?? session?.Runtime.Profile, manifest, exception, true)); }
            }
            entries.Add(new(profile ?? session?.Runtime.Profile, manifest, webManifest));
        }
    }
}
