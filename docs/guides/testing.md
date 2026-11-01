# Testing

`Corfedo.Troupe.Testing` works with any test runner.

## Verify the composition

`CompositionVerifier` builds the host with the application's real builder, resolves declared options, maps web
endpoints and disposes the host, without starting it:

```csharp
var result = await CompositionVerifier.VerifyAsync<ShopComposition>(() => ShopApplication.CreateBuilder([], contentRoot));
Assert.True(result.Succeeded, result.ToString());
```

`VerifyProfilesAsync` does the same for every declared profile, with a fresh builder per profile:

```csharp
var result = await CompositionVerifier.VerifyProfilesAsync<ShopComposition>(
    profile => ShopApplication.CreateBuilder(["--profile", profile.Name], contentRoot));
```

An optional `overrides` callback replaces external resources.

## Compose a slice

`ComposeForTest` composes a test-owned root `test:<name>` that declares like a role, with the definition's stages and
rules. With `withinProfile`, a required component that the profile does not compose fails with TRP020.

```csharp
builder.ComposeForTest<WorkerComposition>(root => root.Requires<WorkerSettingsComponent>(), name: "settings", withinProfile: "worker");
using var host = builder.Build();
```

`FinalizeForTest()` runs the checks `Build` would run and returns the manifest, for a test that inspects a host it
does not build.

## Replace services

On a composed builder, `TestServices(name, services => ...)` adds registrations owned by `test:<name>`; removing
another owner's registration there fails with TRP003. `ReplaceForTest<TService>(name, descriptor)` replaces the one
active registration in a service slot in place, and the manifest links the original to the replacement. A missing
target, or several that the `owner` argument does not narrow to one, fails with TRP019. Overrides go before `Build`.

When one owner registers several descriptors in a slot, a `match` predicate selects exactly one of them, for example
the `IConfigureOptions<HttpClientFactoryOptions>` that sets a typed client's primary handler. Zero or several matches
fail with TRP019, and the message lists the candidates.

`WebApplicationFactory<Program>` works unchanged: the test host is built by Troupe's provider factory and includes
`ConfigureTestServices` registrations.
