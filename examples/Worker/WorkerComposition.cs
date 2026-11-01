using Corfedo.Troupe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe.Examples.Worker;

public sealed class WorkerComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition) => definition.Requires<HeartbeatComponent>();
}

public sealed class HeartbeatComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<WorkerSettingsComponent>();
        component.Services(services => services.AddHostedService<Heartbeat>());
    }
}

public sealed class WorkerSettingsComponent : IComponent
{
    public void Compose(IComponentBuilder component) => component.Options<HeartbeatOptions, HeartbeatValidator>("Worker");
}

public sealed class HeartbeatOptions
{
    public int IntervalSeconds { get; set; }
}

public sealed class HeartbeatValidator : IValidateOptions<HeartbeatOptions>
{
    public ValidateOptionsResult Validate(string? name, HeartbeatOptions options) => options.IntervalSeconds is >= 1 and <= 60
        ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail("Worker:IntervalSeconds must be between 1 and 60.");
}

public sealed class Heartbeat(IOptions<HeartbeatOptions> options, ILogger<Heartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker is active.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.IntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken)) logger.LogInformation("Heartbeat.");
    }
}
