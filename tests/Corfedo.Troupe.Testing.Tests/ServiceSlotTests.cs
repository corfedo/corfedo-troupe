using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Corfedo.Troupe.Testing.Tests.TestHosts;

namespace Corfedo.Troupe.Testing.Tests.ServiceSlots;

// A service slot repeated across owners is reported unless every visible constructor consumer takes IEnumerable<T>.
public sealed class ServiceSlotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateServicesWarnForSingleConsumersAndAllowEnumerableConsumers(bool single)
    {
        var result = await CompositionVerifier.VerifyAsync<SendersDefinition>(() =>
        {
            var builder = Generic();
            builder.Services.AddSingleton<ISender, SenderOne>();
            if (single) builder.Services.AddSingleton<SingleConsumer>();
            else builder.Services.AddSingleton<EnumerableConsumer>();
            return builder;
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(single, result.Profiles.Single().Manifest!.Diagnostics.Any(d => d.Code == "TRP001" && d.Message.Contains(nameof(ISender))));
    }
}

public sealed class SendersDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) => d.Requires<SendersComponent>(); }
public sealed class SendersComponent : IComponent { public void Compose(IComponentBuilder c) => c.Services(s => s.AddSingleton<ISender, SenderTwo>()); }
public interface ISender;
public sealed class SenderOne : ISender;
public sealed class SenderTwo : ISender;
public sealed class SingleConsumer { public SingleConsumer(ISender sender) { } }
public sealed class EnumerableConsumer { public EnumerableConsumer(IEnumerable<ISender> senders) { } }
