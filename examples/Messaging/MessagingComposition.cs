using Corfedo.Troupe.AspNetCore;
using Wolverine;

namespace Corfedo.Troupe.Examples.Messaging;

public sealed class MessagingComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition)
    {
        definition.Role<OrdersRole>().Role<NotificationsRole>()
            .Profile("orders", "orders").Profile("notifications", "notifications")
            .Profile("combined", "orders", "notifications");
        definition.Stages("routing");
    }
}

[Role("orders")]
public sealed class OrdersRole : IRole
{
    public void Compose(IRoleBuilder role)
    {
        role.Requires<OrdersConsumer>().Requires<ReceiptEndpoints>();
        role.Endpoints(endpoints =>
        {
            endpoints.MapPost("/orders", async (PlaceOrder command, IMessageBus bus, MessageJournal journal, CancellationToken token) =>
            {
                var receipt = await bus.InvokeAsync<DeliveryReceipt>(command, token);
                journal.Record(receipt);
                return receipt;
            });
            endpoints.MapPost("/orders/queued", async (PlaceOrder command, IMessageBus bus) =>
            {
                await bus.PublishAsync(command);
                return Results.Accepted($"/receipts/{Uri.EscapeDataString(command.Id)}");
            });
        });
    }
}

[Role("notifications")]
public sealed class NotificationsRole : IRole
{
    public void Compose(IRoleBuilder role)
    {
        role.Requires<NotificationsConsumer>().Requires<ReceiptEndpoints>();
        role.Endpoints(endpoints => endpoints.MapPost("/notifications", async (SendNotification command, IMessageBus bus, MessageJournal journal, CancellationToken token) =>
        {
            var receipt = await bus.InvokeAsync<DeliveryReceipt>(command, token);
            journal.Record(receipt);
            return receipt;
        }));
    }
}

public sealed class OrdersConsumer : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<MessageProcessing>();
        component.Services(services => services.AddSingleton<OrderFormatter>());
        component.Contribute<WolverineOptions>(options =>
        {
            options.Discovery.IncludeType(typeof(PlaceOrderHandler));
            options.PublishMessage<PlaceOrder>().ToLocalQueue("orders");
        });
    }
}

public sealed class NotificationsConsumer : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<MessageProcessing>();
        component.Services(services => services.AddSingleton<NotificationFormatter>());
        component.Contribute<WolverineOptions>(options =>
        {
            options.Discovery.IncludeType(typeof(SendNotificationHandler));
            options.PublishMessage<SendNotification>().ToLocalQueue("notifications");
        });
    }
}

public sealed class MessageProcessing : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Services(services =>
        {
            services.AddSingleton<MessageJournal>();
            services.AddScoped<MessageProcessor>();
        });
        component.Contribute<WolverineOptions>(options => options.Discovery.IncludeType(typeof(DeliveryReceiptHandler)));
    }
}

public sealed class ReceiptEndpoints : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<MessageProcessing>();
        component.KestrelEndpoint("public");
        component.Use("routing", application => application.UseRouting());
        component.Endpoints(endpoints => endpoints.MapGet("/receipts/{id}", (string id, MessageJournal journal) =>
            journal.TryGet(id, out var receipt) ? Results.Ok(receipt) : Results.NotFound()));
    }
}

public sealed class HandlerGenerationComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition) => definition.Role<HandlerGenerationRole>();
}

[Role("generate")]
public sealed class HandlerGenerationRole : IRole
{
    public void Compose(IRoleBuilder role) => role.Requires<OrdersConsumer>().Requires<NotificationsConsumer>();
}
