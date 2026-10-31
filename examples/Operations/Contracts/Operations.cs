namespace Corfedo.Troupe.Examples.Operations;

/// <summary>Names every workload: operation owners and callers.</summary>
public enum Workload { Api, Gateway, Ai }

/// <summary>A request whose owner's handler returns TResponse.</summary>
public interface IOperation<TResponse>;

public sealed record GetWorkspace(string Id) : IOperation<Workspace>;
public sealed record Workspace(string Id, string Name);
public sealed record Summarize(string Text) : IOperation<Summary>;
public sealed record Summary(string Text, int Words);

public interface IOperationHandler<in TRequest, TResponse> where TRequest : IOperation<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

public static class WorkloadNames
{
    /// <summary>The configuration and route name of a workload.</summary>
    public static string Name(this Workload workload) => workload.ToString().ToLowerInvariant();
}
