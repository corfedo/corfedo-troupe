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

    private readonly Stack<CompositionOwner> attribution = new();
    private readonly List<CompositionDiagnostic> violations = [];
    private CompositionOwner Current => attribution.Count > 0 ? attribution.Peek() : Application;
    internal bool IsReadOnly => services.IsReadOnly;

    // Credits collection changes made since the last synchronization to the current attribution owner: the innermost contribution
    // callback, otherwise the application. A contributor's removal of another owner's occurrence has already happened, so it is
    // recorded as accepted and fails with TRP003.
    internal void Synchronize() => Synchronize(guard: true);
    // Records changes for a failure snapshot; a foreign removal is kept for TakeViolations instead of thrown.
    internal void Observe() => Synchronize(guard: false);
    internal CompositionDiagnostic[] TakeViolations() { var taken = violations.ToArray(); violations.Clear(); return taken; }

    private void Synchronize(bool guard)
    {
        var owner = Current;
        var observed = owner == Application;
        var counts = new Dictionary<ServiceDescriptor, int>(ReferenceEqualityComparer.Instance);
        foreach (var descriptor in services) counts[descriptor] = counts.GetValueOrDefault(descriptor) + 1;
        var available = new Dictionary<ServiceDescriptor, Queue<Occurrence>>(ReferenceEqualityComparer.Instance);
        var removed = new List<Occurrence>();
        foreach (var group in live.GroupBy<Occurrence, ServiceDescriptor>(o => o.Descriptor, ReferenceEqualityComparer.Instance))
        {
            // Occurrences of one descriptor instance are indistinguishable in the collection; a removal is matched to the acting
            // owner's own occurrences first, latest first.
            var occurrences = group.ToList();
            var surplus = occurrences.Count - counts.GetValueOrDefault(group.Key);
            if (surplus > 0)
            {
                var victims = Enumerable.Reverse(occurrences).Where(o => o.Owner == owner)
                    .Concat(Enumerable.Reverse(occurrences).Where(o => o.Owner != owner)).Take(surplus).ToHashSet();
                removed.AddRange(occurrences.Where(victims.Contains));
                occurrences.RemoveAll(victims.Contains);
            }
            available.Add(group.Key, new Queue<Occurrence>(occurrences));
        }
        var current = new List<Occurrence>();
        foreach (var descriptor in services)
        {
            if (available.TryGetValue(descriptor, out var queue) && queue.TryDequeue(out var occurrence))
                current.Add(occurrence);
            else
            {
                var added = Create(descriptor, owner);
                Event(observed ? ServiceMutationKind.ApplicationChange : ServiceMutationKind.Add, owner, added, null, true);
                current.Add(added);
            }
        }
        foreach (var occurrence in removed)
        {
            occurrence.RemovedBy = owner;
            Event(observed ? ServiceMutationKind.ApplicationChange : ServiceMutationKind.Remove, owner, occurrence, null, true);
        }
        live.Clear(); live.AddRange(current);
        var foreign = observed ? [] : removed.Where(r => r.Owner != owner).ToArray();
        if (foreign.Length == 0) return;
        var diagnostic = new CompositionDiagnostic("TRP003", DiagnosticSeverity.Error, string.Join(" ", foreign.Select(f =>
                $"{owner.Name} cannot Remove service {f.Descriptor.ServiceType} occurrence {f.Id} owned by {f.Owner.Name}.")),
            Array.AsReadOnly<CompositionOwner>([owner, .. foreign.Select(f => f.Owner).Distinct()]));
        if (guard) throw new CompositionException([diagnostic]);
        violations.Add(diagnostic);
    }

    // Attributes registrations a callback makes directly on the builder's collection to the callback's owner, including those
    // synchronized by validation or nested contributions reached from inside the callback.
    internal void Attribute(CompositionOwner owner, Action callback)
    {
        Synchronize();
        attribution.Push(owner);
        try
        {
            try { callback(); }
            catch { Observe(); throw; }
            Synchronize();
        }
        finally { attribution.Pop(); }
    }

    internal void Configure(CompositionOwner owner, Action<IServiceCollection> callback, bool privileged = false)
    {
        Synchronize();
        var facade = new Facade(this, owner, privileged);
        try { callback(facade); }
        finally { facade.Active = false; }
    }
    // Replaces the single active occurrence in the replacement's type and key slot at its index, regardless of its owner.
    // The target filter narrows candidates by owner; narrowed describes it in diagnostics. The optional descriptor predicate
    // selects among the owner's candidates without depending on their order; its diagnostics list the candidates.
    internal void Replace(CompositionOwner owner, ServiceDescriptor replacement, Func<CompositionOwner, bool>? target, string narrowed,
        Func<ServiceDescriptor, bool>? match) => Configure(owner, services =>
    {
        var slot = $"{replacement.ServiceType}{(replacement.ServiceKey is null ? "" : $" (key {replacement.ServiceKey})")}";
        var inSlot = live.Select((occurrence, index) => (Occurrence: occurrence, Index: index))
            .Where(c => c.Occurrence.Descriptor.ServiceType == replacement.ServiceType && Equals(c.Occurrence.Descriptor.ServiceKey, replacement.ServiceKey))
            .Where(c => target is null || target(c.Occurrence.Owner)).ToArray();
        var candidates = match is null ? inSlot : inSlot.Where(c => match(c.Occurrence.Descriptor)).ToArray();
        if (match is not null && candidates.Length != 1)
        {
            var listed = candidates.Length == 0 ? inSlot : candidates;
            throw Errors.Fail(DiagnosticCodes.TestReplacementTarget, $"{owner.Name}: {candidates.Length} active registrations of service {slot}{narrowed} " +
                $"match the predicate; exactly one must match. {(candidates.Length == 0 ? "Candidates" : "Matches")}: " +
                $"{(listed.Length == 0 ? "none" : string.Join("; ", listed.Select(c => c.Occurrence.Describe())))}.",
                [owner, .. listed.Select(c => c.Occurrence.Owner).Distinct()]);
        }
        if (candidates.Length == 0)
            throw Errors.Fail(DiagnosticCodes.TestReplacementTarget, $"{owner.Name}: service {slot} has no active registration{narrowed} to replace.", owner);
        if (candidates.Length > 1)
            throw Errors.Fail(DiagnosticCodes.TestReplacementTarget, $"{owner.Name}: service {slot} has {candidates.Length} active registrations{narrowed} " +
                $"from {string.Join(", ", candidates.Select(c => c.Occurrence.Owner.Name).Distinct())}; select one owner.",
                [owner, .. candidates.Select(c => c.Occurrence.Owner).Distinct()]);
        services[candidates[0].Index] = replacement;
    }, privileged: true);
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
        internal string Describe()
        {
            var registration = Snapshot();
            var implementation = registration.IsFactory ? "factory" :
                (Descriptor.IsKeyedService ? Descriptor.KeyedImplementationInstance : Descriptor.ImplementationInstance) is { } instance ? $"instance of {instance.GetType()}" :
                $"type {registration.ImplementationType}";
            return $"occurrence {Id} {Descriptor.ServiceType} {implementation} {Descriptor.Lifetime} owned by {Owner.Name}";
        }
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
                ledger.services[index] = value;
                var added = ledger.Create(value, owner);
                ledger.live[index] = added;
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
            EnsureActive();
            // Prefer the acting owner's occurrence when one descriptor instance occurs more than once.
            var index = ledger.live.FindIndex(o => ReferenceEquals(o.Descriptor, item) && o.Owner == owner);
            if (index < 0) index = ledger.services.IndexOf(item);
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
