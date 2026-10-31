namespace Corfedo.Troupe.Examples.Operations;

/// <summary>One operation: its owner, the callers allowed to invoke it and its typed handler.</summary>
public sealed record OperationEntry(string Name, Workload Owner, Type Request, Type Response, IReadOnlySet<Workload> Callers,
    Func<IServiceProvider, object, CancellationToken, Task<object>> Handle);

/// <summary>The component that hosts an owner's handlers in a process.</summary>
public sealed record OwnerHost(Workload Owner, Type Host, Func<IComposition, bool> Included);

/// <summary>The operation catalogue and owner-to-host table, the part a platform would generate.</summary>
public static class OperationTable
{
    public static IReadOnlyList<OperationEntry> Operations { get; } =
    [
        Define<GetWorkspace, Workspace>("workspace.get", Workload.Gateway, Workload.Api),
        Define<Summarize, Summary>("text.summarize", Workload.Ai, Workload.Api)
    ];
    public static IReadOnlyList<OwnerHost> Hosts { get; } =
    [
        new(Workload.Gateway, typeof(GatewayHost), c => c.Includes<GatewayHost>()),
        new(Workload.Ai, typeof(AiHost), c => c.Includes<AiHost>())
    ];
    private static readonly Dictionary<string, OperationEntry> byName = Operations.ToDictionary(o => o.Name, StringComparer.Ordinal);
    private static readonly Dictionary<Type, OperationEntry> byRequest = Operations.ToDictionary(o => o.Request);

    public static OperationEntry? Find(string name) => byName.GetValueOrDefault(name);
    public static OperationEntry For(Type request) => byRequest.TryGetValue(request, out var entry) ? entry :
        throw new InvalidOperationException($"{request.FullName} is not a catalogued operation.");

    private static OperationEntry Define<TRequest, TResponse>(string name, Workload owner, params Workload[] callers)
        where TRequest : IOperation<TResponse> where TResponse : notnull =>
        new(name, owner, typeof(TRequest), typeof(TResponse), callers.ToHashSet(),
            async (services, request, cancellationToken) => await services.GetRequiredService<IOperationHandler<TRequest, TResponse>>()
                .HandleAsync((TRequest)request, cancellationToken));
}
