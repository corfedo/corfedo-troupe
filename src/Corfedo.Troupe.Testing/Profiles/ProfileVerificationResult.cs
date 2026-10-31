using Corfedo.Troupe.AspNetCore;

namespace Corfedo.Troupe.Testing;

/// <summary>Records an attempted profile and the available immutable snapshots.</summary>
/// <param name="Profile">The declared serving profile.</param><param name="Manifest">The core snapshot, if composition reached recording.</param>
/// <param name="AspNetManifest">The web snapshot, when applicable.</param>
public sealed record ProfileVerificationEntry(ProfileIdentity Profile, CompositionManifest? Manifest, AspNetCompositionManifest? AspNetManifest);
/// <summary>Associates a verification or cleanup failure with its profile.</summary>
/// <param name="Profile">The failed serving profile.</param><param name="Manifest">The available core snapshot.</param>
/// <param name="Exception">The original failure.</param><param name="DuringDisposal">Whether the failure came from host cleanup.</param>
public sealed record ProfileVerificationFailure(ProfileIdentity Profile, CompositionManifest? Manifest, Exception Exception, bool DuringDisposal);
/// <summary>Aggregates independent profile results without depending on a test runner.</summary>
public sealed class ProfileVerificationResult
{
    internal ProfileVerificationResult(IEnumerable<ProfileVerificationEntry> profiles, IEnumerable<ProfileVerificationFailure> failures)
    { Profiles = Array.AsReadOnly(profiles.ToArray()); Failures = Array.AsReadOnly(failures.ToArray()); }
    /// <summary>Gets every attempted profile.</summary>
    public IReadOnlyList<ProfileVerificationEntry> Profiles { get; }
    /// <summary>Gets composition, build, mapping, option and disposal failures.</summary>
    public IReadOnlyList<ProfileVerificationFailure> Failures { get; }
    /// <summary>Gets whether every declared profile verified and disposed successfully.</summary>
    public bool Succeeded => Failures.Count == 0;
    /// <summary>Formats failures for a test assertion.</summary>
    public override string ToString() => Succeeded ? $"Verified {Profiles.Count} profiles." : string.Join(Environment.NewLine,
        Failures.Select(f => $"{f.Profile.Name}{(f.DuringDisposal ? " (disposal)" : "")}: {f.Exception}"));
}
