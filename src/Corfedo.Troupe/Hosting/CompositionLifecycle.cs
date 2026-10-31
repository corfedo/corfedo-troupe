using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Corfedo.Troupe;

internal sealed record StartupCheck(CompositionOwner Owner, Func<IServiceProvider, CancellationToken, Task> Callback);

internal sealed class CompositionLifecycle(CompositionSession session, IServiceProvider services, ILogger<CompositionLifecycle> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        session.Validate(finalize: true);
        foreach (var check in session.Checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await check.Callback(services, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { throw new CompositionCheckException(check.Owner, exception); }
        }
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Troupe roles: {Roles}; components: {Components}",
            string.Join(", ", session.Roles.Select(r => r.Name)),
            string.Join(", ", session.Nodes.Where(n => n.Owner.Kind == OwnerKind.Component).Select(n => n.Owner.Name)));
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
