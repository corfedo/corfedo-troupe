# Split-readiness analysis

The question for each candidate boundary: if the parts on either side ran in different processes, possibly with
several instances each, would the application still be correct? Troupe answers only which components a process
composes. Everything below comes from reading the code, so state for each finding what you read and what you could not
confirm.

## Candidates

A candidate boundary separates sets of components that would become roles: existing Host projects, areas the user names,
or groups with separate scaling or availability needs. Start from the requirement graph (`manifest.Contributors` and
`PathTo<T>()`, or the proposal's graph): a component required from both sides runs in both processes, which is correct
only if it holds no state that the two sides must share.

## What to look for

For each finding record the file and line, the parts on both sides, and the consequence when split.

| Concern | How to find it | Consequence when split |
| --- | --- | --- |
| Shared in-memory state | singletons holding mutable fields or collections (`ConcurrentDictionary`, `List<>`, `Dictionary<>`), `IMemoryCache`, `static` mutable fields, `Lazy<T>` caches; resolve which components use each | each process has its own copy; reads on one side miss writes on the other |
| In-process calls across parts | constructor parameters of services from another part's component; `IMediator.Send`/`Publish`, `INotificationHandler`, local event aggregators, `Channel<T>`, Wolverine local queues, `IMessageBus.InvokeAsync` to a handler of another part | the callee is not composed in the caller's process; needs a remote call or a durable message |
| Fan-in through `IEnumerable<T>` | services resolved as `IEnumerable<T>` or `GetServices<T>()` whose implementations come from several parts (health checks, readiness, validators, plugins) | silently empty or partial in a split process; no error |
| One-instance assumptions | `BackgroundService`, `PeriodicTimer`, `Timer`, Quartz or Hangfire jobs, `Database.Migrate()` or seeding at startup, in-process leader flags | runs once per process and per instance: duplicate work, migration races |
| Machine resources | `File.`, `Directory.`, `Path.GetTempPath`, local upload folders, `lock`/`SemaphoreSlim`/`Mutex` coordinating requests, SQLite or in-memory databases | files and locks are not shared between processes or machines |
| Per-process web state | `AddDataProtection()` without a shared key store, `AddDistributedMemoryCache`, in-memory session, output cache, rate limiters, antiforgery, cookie authentication issued by one part and read by another | a cookie or token issued in one process cannot be read in another; limits apply per process |
| Real-time connections | SignalR hubs, WebSockets, server-sent events, groups and `IHubContext` used from other parts | messages sent from one process do not reach clients connected to another without a backplane |
| Transactions across parts | one `DbContext.SaveChanges`, `TransactionScope` or `BeginTransaction` writing tables of two parts; row locks one part takes for another. Find the operation that owns the transaction and the process it would run in | an operation that moves whole keeps its transaction; one whose writes would land in different processes loses atomicity and needs an outbox or saga, or the parts stay together |
| Configuration per process | sections only one side reads; addresses of the other side | declare `ReadsConfigurationUnless<TOther>` for the address; each deployment gets only its sections |
| Listeners | `KestrelEndpoint` declarations, ports, TLS certificates per part | each profile requests only its endpoints (TRP103) |
| Discovery | controllers or scanned endpoints of several parts in one assembly; `AddControllers()` without application-part control | every process maps every part's endpoints; move each part's controllers into its own assembly and add its application part from its component |
| Identity and authorization | the authentication scheme setup, token validation and policies each part needs | each process needs its own validation setup; a part that issues tokens cannot be assumed local |

## Verdicts

One verdict per boundary:

- **Ready:** every concern in the table was examined, and the findings are only configuration and listeners. The
  roles can be added as they are.
- **Needs a contract:** the parts interact through findings that have a cross-process replacement: a durable message
  (outbox) for events and fire-and-forget work, a remote call for queries and commands that need an answer, a shared
  store for cache, data protection keys or sessions, a lease or leader election for one-instance jobs. List each
  contract with the code it replaces. Designing and building them is separate work the user approves.
- **Keep together:** the boundary would divide an atomic operation's writes between processes, the interaction is too chatty to cross a network, or shared
  in-memory state is the point of the design. Recommend one role for both.
- **Inconclusive:** a concern could not be examined or a finding could not be confirmed from the code (reflection,
  dynamic registration, behavior in code outside the repository). Name what would settle it. An inconclusive boundary
  is not split until it is settled or the user accepts the risk explicitly.

## Checking a split with Troupe

After the user chooses roles and the contracts they need exist, declare the profiles and run
`CompositionVerifier.VerifyProfilesAsync<T>(profile => App.CreateBuilder(["--profile", profile.Name]))`. A constructor
dependency on a component the profile does not compose fails `Build` with the missing service. It does not find:

- dependencies resolved inside service factories (`AddSingleton(provider => ...)`); read them;
- `IEnumerable<T>` consumers that become empty;
- minimal API parameters whose service is missing: the parameter is inferred as the request body. On `GET` mapping
  fails; on `POST` and `PUT` requests bind a new instance from the body or fail, so read the handlers' parameters.

The verifier does not start the host, so start each profile in a test as well. Report the results together with these
readings.

## Report format

```text
Boundary: <parts on side A> | <parts on side B>
Verdict: ready | needs a contract | keep together | inconclusive
Findings:
- <concern>: <file>:<line> — <what is shared or called> — <consequence> — <replacement or reason to keep together>
Not confirmed: <what could not be determined from the code>
```
