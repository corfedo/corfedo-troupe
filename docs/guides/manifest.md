# The manifest

Every process can tell what it composes. `AddTroupe` returns a `CompositionManifest`, and the built host provides the
final one through `IComposition.Manifest`: the selected profile and roles, if any, contributors in execution order, every
service registration with its owner, configuration reads, contributions and diagnostics.

```csharp
var manifest = host.Services.GetRequiredService<IComposition>().Manifest;
bool processing = manifest.Composes<MessageProcessing>();
var path = manifest.PathTo<MessageProcessing>();   // the role or definition, then each component down to MessageProcessing
var registrations = manifest.OwnedBy(path[^1]).Where(s => s.Active);
var readers = manifest.Reads("Storage").Select(r => r.Owner);
var wolverine = manifest.ContributorsTo<WolverineOptions>();
```

`Active(serviceType, key)` lists the registrations in one service slot. Removed and replaced registrations stay in
the manifest with `RemovedBy` and `ReplacedBy`.

## Rules

An `ICompositionRule` added with `definition.Rule(...)` evaluates application policy against the manifest, for example
that every owner a caller declares is exposed to it. Its diagnostics are reported like Troupe's own: errors fail
composition, advisories are listed in `Diagnostics`.

```csharp
public sealed class NoDirectDatabaseAccess : ICompositionRule
{
    public IEnumerable<CompositionDiagnostic> Evaluate(CompositionManifest manifest) =>
        manifest.Active(typeof(NpgsqlDataSource)).Where(s => s.Owner.Type != typeof(Database))
            .Select(s => new CompositionDiagnostic("APP001", DiagnosticSeverity.Error,
                $"{s.Owner.Name} registers a data source; require Database instead.", [s.Owner]));
}
```
