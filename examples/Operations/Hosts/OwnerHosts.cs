namespace Corfedo.Troupe.Examples.Operations;

/// <summary>Hosts Gateway's handlers; Gateway operations are exposed, so the host requires the internal listener.</summary>
public sealed class GatewayHost : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<OperationCatalogue>().Requires<InternalListener>();
        component.Services(services => services.AddScoped<IOperationHandler<GetWorkspace, Workspace>, WorkspaceHandler>());
    }
}

/// <summary>Hosts AI's handlers; AI operations are exposed, so the host requires the internal listener.</summary>
public sealed class AiHost : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<OperationCatalogue>().Requires<InternalListener>();
        component.Services(services => services.AddScoped<IOperationHandler<Summarize, Summary>, SummaryHandler>());
    }
}

internal sealed class WorkspaceHandler : IOperationHandler<GetWorkspace, Workspace>
{
    public Task<Workspace> HandleAsync(GetWorkspace request, CancellationToken cancellationToken) =>
        Task.FromResult(new Workspace(request.Id, $"Workspace {request.Id}"));
}

internal sealed class SummaryHandler : IOperationHandler<Summarize, Summary>
{
    public Task<Summary> HandleAsync(Summarize request, CancellationToken cancellationToken)
    {
        var words = request.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Task.FromResult(new Summary(string.Join(' ', words.Take(3)), words.Length));
    }
}
