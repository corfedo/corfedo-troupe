namespace Corfedo.Troupe.Examples.Messaging;

public sealed record PlaceOrder(string Id);
public sealed record SendNotification(string Id, string Recipient);
public sealed record DeliveryReceipt(string Id, string Consumer, string Message, Guid ScopeId);

public static class PlaceOrderHandler
{
    public static DeliveryReceipt Handle(PlaceOrder command, MessageProcessor processor, OrderFormatter formatter) =>
        processor.Process(command.Id, "orders", formatter.Format(command.Id));
}

public static class SendNotificationHandler
{
    public static DeliveryReceipt Handle(SendNotification command, MessageProcessor processor, NotificationFormatter formatter) =>
        processor.Process(command.Id, "notifications", formatter.Format(command.Recipient));
}

public sealed class OrderFormatter
{
    public string Format(string id) => $"Order {id} processed";
}

public sealed class NotificationFormatter
{
    public string Format(string recipient) => $"Notification sent to {recipient}";
}

public static class DeliveryReceiptHandler
{
    public static void Handle(DeliveryReceipt receipt, MessageJournal journal) => journal.Record(receipt);
}
