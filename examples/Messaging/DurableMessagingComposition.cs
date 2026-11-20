using Microsoft.Extensions.Configuration;
using Corfedo.Troupe.AspNetCore;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Postgresql;

namespace Corfedo.Troupe.Examples.Messaging;

public sealed class DurableMessagingComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition)
    {
        definition.Profile("orders", p => p.Role<DurableOrdersRole>()).Profile("notifications", p => p.Role<DurableNotificationsRole>())
            .Profile("combined", p => p.Role<DurableOrdersRole>().Role<DurableNotificationsRole>())
            .Profile("publisher", p => p.Role<PublisherRole>());
        definition.Stages("routing");
    }
}

public sealed class DurableOrdersRole : IRole
{
    public static string Name => "orders";
    public void Compose(IRoleBuilder role) => role.Requires<OrdersQueue>().Requires<ReceiptEndpoints>();
}

public sealed class DurableNotificationsRole : IRole
{
    public static string Name => "notifications";
    public void Compose(IRoleBuilder role) => role.Requires<NotificationsQueue>().Requires<ReceiptEndpoints>();
}

public sealed class OrdersQueue : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<PostgresMessaging>().Requires<OrdersConsumer>();
        component.Contribute<WolverineOptions>(options => options.ListenToPostgresqlQueue("orders").UseDurableInbox());
    }
}

public sealed class NotificationsQueue : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<PostgresMessaging>().Requires<NotificationsConsumer>();
        component.Contribute<WolverineOptions>(options => options.ListenToPostgresqlQueue("notifications").UseDurableInbox());
    }
}

public sealed class PostgresMessaging : IComponent
{
    public void Compose(IComponentBuilder component) =>
        component.Options<PostgresMessagingOptions, PostgresMessagingValidator>("Messaging", (section, options) => section.Bind(options));
}

public sealed class PostgresMessagingOptions
{
    public string ConnectionString { get; set; } = "";
}

public sealed class PostgresMessagingValidator : IValidateOptions<PostgresMessagingOptions>
{
    public ValidateOptionsResult Validate(string? name, PostgresMessagingOptions options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? ValidateOptionsResult.Fail("Messaging:ConnectionString is required.") : ValidateOptionsResult.Success;
}

public sealed class PublisherRole : IRole
{
    public static string Name => "publisher";
    public void Compose(IRoleBuilder role)
    {
        role.Requires<PostgresMessaging>();
        role.KestrelEndpoint("public");
        role.Use("routing", application => application.UseRouting());
        role.Endpoints(endpoints =>
        {
            endpoints.MapPost("/orders/queued", async (PlaceOrder command, IMessageBus bus) =>
            {
                await bus.EndpointFor(new Uri("postgresql://orders")).SendAsync(command);
                return Results.Accepted();
            });
            endpoints.MapPost("/notifications/queued", async (SendNotification command, IMessageBus bus) =>
            {
                await bus.EndpointFor(new Uri("postgresql://notifications")).SendAsync(command);
                return Results.Accepted();
            });
        });
    }
}

public sealed class DurableProvisioningComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition) => definition.Profile("provision", p => p.Role<DurableProvisioningRole>());
}

public sealed class DurableProvisioningRole : IRole
{
    public static string Name => "provision";
    public void Compose(IRoleBuilder role) => role.Requires<OrdersQueue>().Requires<NotificationsQueue>();
}
