namespace Corfedo.Troupe.Examples.Operations;

/// <summary>Shared by every owner host: registers the invoker once.</summary>
public sealed class OperationCatalogue : IComponent
{
    public void Compose(IComponentBuilder component) => component.Services(services => services.AddSingleton<OperationInvoker>());
}

/// <summary>Thrown when the caller policy does not allow a caller to invoke an operation.</summary>
public sealed class OperationDeniedException(Workload caller, OperationEntry operation)
    : Exception($"{caller.Name()} may not invoke {operation.Name}.");

/// <summary>Runs operations of owners hosted in this process, for local and remote callers alike.</summary>
public sealed class OperationInvoker(IComposition composition, IServiceScopeFactory scopes)
{
    // Resolved once: each call is a set lookup.
    private readonly HashSet<Workload> hosted = OperationTable.Hosts.Where(h => h.Included(composition)).Select(h => h.Owner).ToHashSet();

    public async Task<object> InvokeAsync(Workload caller, OperationEntry operation, object request, CancellationToken cancellationToken)
    {
        if (!operation.Callers.Contains(caller)) throw new OperationDeniedException(caller, operation);
        if (!hosted.Contains(operation.Owner)) throw new InvalidOperationException($"{operation.Owner.Name()} is not hosted in this process.");
        await using var scope = scopes.CreateAsyncScope();
        return await operation.Handle(scope.ServiceProvider, request, cancellationToken);
    }
}
