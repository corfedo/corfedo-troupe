using System.Collections.Concurrent;

namespace Corfedo.Troupe.Examples.Messaging;

public sealed class MessageJournal
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DeliveryReceipt>> receipts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, byte> disposedScopes = [];

    public bool TryGet(string id, out DeliveryReceipt? receipt)
    {
        receipt = receipts.TryGetValue(id, out var completion) && completion.Task.IsCompletedSuccessfully ? completion.Task.Result : null;
        return receipt is not null;
    }
    public Task<DeliveryReceipt> WaitForAsync(string id, CancellationToken cancellationToken) =>
        Completion(id).Task.WaitAsync(cancellationToken);
    public bool IsScopeDisposed(Guid scopeId) => disposedScopes.ContainsKey(scopeId);
    internal void Record(DeliveryReceipt receipt) => Completion(receipt.Id).TrySetResult(receipt);
    internal void Disposed(Guid scopeId) => disposedScopes.TryAdd(scopeId, 0);
    private TaskCompletionSource<DeliveryReceipt> Completion(string id) => receipts.GetOrAdd(id,
        _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

public sealed class MessageProcessor(MessageJournal journal) : IDisposable
{
    private readonly Guid scopeId = Guid.NewGuid();
    public DeliveryReceipt Process(string id, string consumer, string message)
    {
        return new DeliveryReceipt(id, consumer, message, scopeId);
    }
    public void Dispose() => journal.Disposed(scopeId);
}
