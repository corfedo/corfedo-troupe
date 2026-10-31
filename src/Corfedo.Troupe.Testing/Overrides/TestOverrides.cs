using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe.Testing;

/// <summary>Adds and replaces services on a composed builder with test ownership recorded in the manifest.</summary>
/// <remarks>
/// Both operations record <see cref="OwnerKind.Test"/> named <c>test:&lt;name&gt;</c>, run after Compose and before or after
/// FinalizeComposition, and revalidate the composition. Pipeline application and startup revalidate it again.
/// </remarks>
public static class TestOverrides
{
    /// <summary>Records test-owned service registrations under the ordinary ownership check.</summary>
    /// <remarks>Removing or replacing a registration of another owner fails with TRP003; use <see cref="ReplaceForTest{TService}"/> to replace one.</remarks>
    /// <param name="builder">A builder composed by Troupe.</param><param name="name">The test owner name.</param>
    /// <param name="add">A synchronous callback; do not retain the supplied collection.</param>
    public static void TestServices(this IHostApplicationBuilder builder, string name, Action<IServiceCollection> add)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(add);
        var session = CompositionSession.Get(builder);
        session.ApplyTestOverrides(name, add, privileged: false);
        session.Validate();
    }

    /// <summary>Replaces the one active registration of <typeparamref name="TService"/> in the key slot in place, whoever owns it.</summary>
    /// <remarks>
    /// The manifest links the original to the replacement through RemovedBy and ReplacedBy. A missing target, or several active
    /// registrations that <paramref name="owner"/> does not narrow to one, fails with TRP019.
    /// </remarks>
    /// <param name="builder">A builder composed by Troupe.</param><param name="name">The test owner name.</param>
    /// <param name="replacement">The replacing descriptor, for <typeparamref name="TService"/> and <paramref name="key"/>.</param>
    /// <param name="key">The service key; null denotes the unkeyed slot.</param>
    /// <param name="owner">The role or component type that registered the target, when the slot has several registrations.</param>
    public static void ReplaceForTest<TService>(this IHostApplicationBuilder builder, string name,
        ServiceDescriptor replacement, object? key = null, Type? owner = null)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.ServiceType != typeof(TService) || !Equals(replacement.ServiceKey, key))
            throw new ArgumentException($"The replacement must register {typeof(TService)} with the requested key.", nameof(replacement));
        var session = CompositionSession.Get(builder);
        session.ReplaceForTest(name, replacement, owner);
        session.Validate();
    }
}
