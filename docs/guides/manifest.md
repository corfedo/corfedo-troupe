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

## Describing a process

When the environment variable `TROUPE_DESCRIBE` names a file, `AddTroupe` composes the process's own selection, writes
a JSON description to that file and ends the process with exit code 0. The selection comes from the process's own
command line, configuration sources and environment, so a tool describes a deployment's process by running it with
that process's arguments and environment. Code before `AddTroupe` runs; code after it never does.

```sh
TROUPE_DESCRIBE=api.json dotnet run --project examples/Operations -- --profile api
```

```json
{
  "format": 1,
  "definition": "Corfedo.Troupe.Examples.Operations.OperationsComposition",
  "profiles": [ { "name": "api", "roles": ["api"] }, "…" ],
  "roles": ["ai", "api", "gateway"],
  "selection": { "profile": "api", "roles": ["api"] },
  "components": ["Corfedo.Troupe.Examples.Operations.ApiCalls", "Corfedo.Troupe.Examples.Operations.PublicIngress", "…"],
  "listeners": [ { "name": "public", "owners": ["Corfedo.Troupe.Examples.Operations.PublicIngress"] } ],
  "configuration": [
    {
      "section": "Roles:Remote:gateway", "present": false, "owners": ["Corfedo.Troupe.Examples.Operations.ApiCalls"],
      "unlessComposed": ["Corfedo.Troupe.Examples.Operations.GatewayHost"]
    }
  ],
  "diagnostics": [ { "code": "TRP005", "severity": "Error", "message": "…" } ]
}
```

- `profiles` and `roles` list everything the definition declares. `selection` is what this process composes, or null
  when selection failed; `diagnostics` say why.
- `components` and `listeners` are what this process composes and the named Kestrel endpoints it requests, with the
  contributors that require each one.
- `configuration` lists each section this process needs once: required reads, and `ReadsConfigurationUnless` reads
  whose component is not composed. `unlessComposed` names those components and is absent when a read is required.
  `present` is evaluated against the process's configuration; values are never written.
- `diagnostics` are the errors and advisories the same composition reports at startup. A composition error does not
  stop the description.

Lists are in ordinal order. The file is written to a temporary file next to it and then moved into place.
