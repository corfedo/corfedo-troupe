using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe;

// Build writes HostBuilding before it applies the callbacks that host-building listeners (WebApplicationFactory, design-time
// tools) queue on the IHostBuilder adapter; the adapter shares the builder's Properties, which identify the composition.
// One observer subscription serves the process and is never disposed; each hosting listener drops its subscribers when Build
// disposes it. Compositions are held weakly by their builder's Properties, so a builder that is never built, or whose
// composition failed, stays collectable. The callbacks take no lock of their own: DiagnosticListener calls them while it holds
// its global listener lock.
internal sealed class HostBuildingObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private static readonly ConditionalWeakTable<IDictionary<object, object>, CompositionSession> Pending = new();
    private static readonly HostBuildingObserver Instance = new();
    private static IDisposable? subscription;

    internal static void Watch(IDictionary<object, object> properties, CompositionSession session)
    {
        Pending.AddOrUpdate(properties, session);
        if (Volatile.Read(ref subscription) is not null) return;
        // Until one subscription has completed, each first composition subscribes itself and keeps the first that completes. Waiting
        // for another thread's subscription instead could deadlock: it waits for DiagnosticListener's global lock, which this thread
        // may hold (a listener callback that composes a host). A duplicate subscription only repeats HostBuilding, which
        // starts host building once.
        var own = DiagnosticListener.AllListeners.Subscribe(Instance);
        if (Interlocked.CompareExchange(ref subscription, own, null) is not null) own.Dispose();
    }
    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.Extensions.Hosting") listener.Subscribe(this, name => name == "HostBuilding");
    }
    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != "HostBuilding" || value.Value is not IHostBuilder host) return;
        if (Pending.TryGetValue(host.Properties, out var session) && Pending.Remove(host.Properties)) session.BeginHostBuilding();
    }
    public void OnCompleted() { }
    public void OnError(Exception error) { }
}
