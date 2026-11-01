using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Corfedo.Troupe;

internal sealed class StartupCheck(CompositionOwner owner, Func<IServiceProvider, CancellationToken, Task> callback)
{
    internal CompositionOwner Owner { get; } = owner;
    internal Func<IServiceProvider, CancellationToken, Task>? Callback = callback;
}

internal sealed class CompositionLifecycle(CompositionSession session, IServiceProvider services, ILogger<CompositionLifecycle> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => session.RunChecksAsync(services, cancellationToken);
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        session.CompleteStart();
        var components = string.Join(", ", session.Runtime.Manifest.Contributors.Where(n => n.Owner.Kind == OwnerKind.Component).Select(n => n.Owner.Name));
        if (session.Runtime.Roles.Count == 0) logger.LogInformation("Troupe components: {Components}", components);
        else logger.LogInformation("Troupe profile: {Profile}; roles: {Roles}; components: {Components}", session.Runtime.Profile?.Name ?? "(none)",
            string.Join(", ", session.Runtime.Roles.Select(r => r.Name)), components);
        return Task.CompletedTask;
    }
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Associates a failed startup check with its declaration owner.</summary>
public sealed class CompositionCheckException : Exception
{
    internal CompositionCheckException(CompositionOwner owner, Exception innerException)
        : base($"Startup check owned by {owner.Name} failed.", innerException) => Owner = owner;
    /// <summary>Gets the contributor whose check failed.</summary>
    public CompositionOwner Owner { get; }
}
