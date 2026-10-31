using System.Collections;
using Microsoft.Extensions.DependencyInjection;

namespace Corfedo.Troupe;

internal sealed class ServiceLedger(IServiceCollection collection)
{
    private readonly IServiceCollection services = collection;
    internal static readonly CompositionOwner Application = new(OwnerKind.Application, "application");
    private readonly List<Occurrence> history = [];
    private readonly List<Occurrence> live = [];
    private readonly List<ServiceMutation> mutations = [];
    private long sequence;

    internal void Synchronize()
    {
        var available = live.GroupBy<Occurrence, ServiceDescriptor>(o => o.Descriptor, ReferenceEqualityComparer.Instance)
            .ToDictionary(g => g.Key, g => new Queue<Occurrence>(g), (IEqualityComparer<ServiceDescriptor>)ReferenceEqualityComparer.Instance);
        var current = new List<Occurrence>();
        foreach (var descriptor in services)
        {
            if (available.TryGetValue(descriptor, out var queue) && queue.TryDequeue(out var occurrence))
                current.Add(occurrence);
            else
            {
                var added = Create(descriptor, Application);
                Event(ServiceMutationKind.ApplicationChange, Application, added, null, true);
                current.Add(added);
            }
        }
        foreach (var removed in available.Values.SelectMany(q => q))
        {
            removed.RemovedBy = Application;
            Event(ServiceMutationKind.ApplicationChange, Application, removed, null, true);
        }
        live.Clear(); live.AddRange(current);
    }

    internal void Configure(CompositionOwner owner, Action<IServiceCollection> callback, bool privileged = false)
    {
        Synchronize();
        var facade = new Facade(this, owner, privileged);
        try { callback(facade); }
        finally { facade.Active = false; }
    }
    internal ServiceRegistration[] Registrations() => history.Select(o => o.Snapshot()).ToArray();
    internal ServiceMutation[] Mutations() => mutations.ToArray();
    private Occurrence Create(ServiceDescriptor descriptor, CompositionOwner owner)
    {
        var occurrence = new Occurrence(history.Count + 1, descriptor, owner);
        history.Add(occurrence); return occurrence;
    }
    private void Event(ServiceMutationKind kind, CompositionOwner owner, Occurrence occurrence, Occurrence? replacement, bool accepted) =>
        mutations.Add(new(++sequence, kind, owner, occurrence.Id, replacement?.Id, accepted));
    private void Guard(CompositionOwner owner, bool privileged, Occurrence target, ServiceMutationKind kind)
    {
        if (privileged || target.Owner == owner) return;
        Event(kind, owner, target, null, false);
        throw Errors.Fail("TRP003", $"{owner.Name} cannot {kind} service {target.Descriptor.ServiceType} occurrence {target.Id} owned by {target.Owner.Name}.", owner, target.Owner);
    }

    private sealed class Occurrence(long id, ServiceDescriptor descriptor, CompositionOwner owner)
    {
        internal long Id { get; } = id;
        internal ServiceDescriptor Descriptor { get; } = descriptor;
        internal CompositionOwner Owner { get; } = owner;
        internal CompositionOwner? RemovedBy;
        internal long? ReplacedBy;
        internal ServiceRegistration Snapshot()
        {
            var implementation = Descriptor.IsKeyedService ? Descriptor.KeyedImplementationType : Descriptor.ImplementationType;
            var instance = Descriptor.IsKeyedService ? Descriptor.KeyedImplementationInstance : Descriptor.ImplementationInstance;
            var factory = Descriptor.IsKeyedService ? Descriptor.KeyedImplementationFactory is not null : Descriptor.ImplementationFactory is not null;
            return new(Id, Owner, Descriptor.ServiceType, Descriptor.Lifetime, Descriptor.ServiceKey,
                implementation ?? instance?.GetType(), factory, RemovedBy is null, RemovedBy, ReplacedBy);
        }
    }
    private sealed class Facade(ServiceLedger ledger, CompositionOwner owner, bool privileged) : IServiceCollection
    {
        internal bool Active = true;
        private void EnsureActive() { if (!Active) throw new InvalidOperationException("The service callback has completed; do not retain its collection."); }
        public int Count { get { EnsureActive(); return ledger.services.Count; } }
        public bool IsReadOnly => false;
        public ServiceDescriptor this[int index]
        {
            get { EnsureActive(); return ledger.services[index]; }
            set
            {
                EnsureActive(); ArgumentNullException.ThrowIfNull(value);
                var old = ledger.live[index];
                ledger.Guard(owner, privileged, old, ServiceMutationKind.Replace);
                var added = ledger.Create(value, owner);
                ledger.services[index] = value; ledger.live[index] = added;
                old.RemovedBy = owner; old.ReplacedBy = added.Id;
                ledger.Event(ServiceMutationKind.Replace, owner, old, added, true);
            }
        }
        public void Add(ServiceDescriptor item) => InsertCore(Count, item, ServiceMutationKind.Add);
        public void Insert(int index, ServiceDescriptor item) => InsertCore(index, item, ServiceMutationKind.Insert);
        private void InsertCore(int index, ServiceDescriptor item, ServiceMutationKind kind)
        {
            EnsureActive(); ArgumentNullException.ThrowIfNull(item);
            ledger.services.Insert(index, item);
            var occurrence = ledger.Create(item, owner); ledger.live.Insert(index, occurrence);
            ledger.Event(kind, owner, occurrence, null, true);
        }
        public bool Remove(ServiceDescriptor item)
        {
            EnsureActive(); var index = ledger.services.IndexOf(item);
            if (index < 0) return false;
            RemoveCore(index, ServiceMutationKind.Remove); return true;
        }
        public void RemoveAt(int index) => RemoveCore(index, ServiceMutationKind.RemoveAt);
        private void RemoveCore(int index, ServiceMutationKind kind)
        {
            EnsureActive(); var occurrence = ledger.live[index];
            ledger.Guard(owner, privileged, occurrence, kind);
            ledger.services.RemoveAt(index); ledger.live.RemoveAt(index); occurrence.RemovedBy = owner;
            ledger.Event(kind, owner, occurrence, null, true);
        }
        public void Clear()
        {
            EnsureActive();
            foreach (var occurrence in ledger.live) ledger.Guard(owner, privileged, occurrence, ServiceMutationKind.Clear);
            while (ledger.live.Count > 0) RemoveCore(ledger.live.Count - 1, ServiceMutationKind.Clear);
        }
        public bool Contains(ServiceDescriptor item) { EnsureActive(); return ledger.services.Contains(item); }
        public int IndexOf(ServiceDescriptor item) { EnsureActive(); return ledger.services.IndexOf(item); }
        public void CopyTo(ServiceDescriptor[] array, int arrayIndex) { EnsureActive(); ledger.services.CopyTo(array, arrayIndex); }
        public IEnumerator<ServiceDescriptor> GetEnumerator() { EnsureActive(); return ledger.services.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
