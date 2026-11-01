# Composition patterns and their Troupe targets

Each row names what a migration meets, where it goes, and the guide that documents the target. Guides are in
`docs/guides/` of the Troupe repository at the tag of the version in use
(`https://github.com/corfedo/corfedo-troupe/tree/v<version>/docs/guides`). Behavior notes were checked against Troupe
0.2.0, MassTransit 8.5 and MediatR 12.5.

## Services and configuration

| Pattern | Target | Guide |
| --- | --- | --- |
| `services.AddX()` of one area | `component.Services(services => services.AddX())` | composition |
| `AddX()` calling another area's `AddY()` for its services | `Requires<YComponent>()` when X's project references Y's; otherwise the definition requires both | composition |
| `services.Configure<T>(configuration.GetSection("S"))`, `AddOptions<T>().BindConfiguration("S")` | `Options<T>("S")`; `Options<T>("S", required: false)` when the section is optional | configuration |
| `ValidateDataAnnotations()`, `Validate(...)`, `IValidateOptions<T>` | `Options<T, TValidator>("S")`, or `Options<T>` plus `services.AddOptions<T>().ValidateDataAnnotations()` in `Services` | configuration |
| Named options `Configure<T>("name", ...)` | `services.AddOptions<T>("name").BindConfiguration("S:name")` in `Services`, plus `ReadsConfiguration("S:name")`; `Options<T>` has no names | configuration |
| A value read at registration (`configuration["X"]`, `GetConnectionString`) | `ReadsConfiguration("X")`, and read `IConfiguration` from the provider in a factory: `AddDbContext<T>((provider, options) => ...)`, `AddSingleton(provider => ...)` | configuration |
| A registration chosen by configuration (`if (kind == "s3")`) | register the candidates and choose in a factory. A candidate with its own hosted services, listeners or required sections is a component of its own; configuration never chooses which components compose (the definition or a profile does). A choice that fits neither is a gap to report | composition |
| A registration chosen by environment (`IsDevelopment()`) | a validator or factory that takes `IHostEnvironment`; otherwise as a configuration choice | composition |
| Address of a part that runs elsewhere | `ReadsConfigurationUnless<TOwnerComponent>("S")` | configuration |
| `TryAdd*` | works; a registration the application made before `AddTroupe` wins silently and the component's is not recorded | manifest |
| Two areas register one service type | TRP001 advisory; keep one owner, or `MultipleAllowed<T>()` when intended | diagnostics |
| A service that must not have several registrations | `ExclusiveService<T>()` (TRP004); it allows none, so presence still comes from a consumer or `ValidateOnBuild` | composition |
| `Replace`/`RemoveAll` of another area's registration | fails with TRP003 inside a component; move the decision to the owner. In `Program.cs` it passes silently | diagnostics |
| `AddHostedService<T>()`, `BackgroundService` | `Services(...)`; `AddHostedService` deduplicates by type, an explicit `AddSingleton<IHostedService, T>` twice is TRP002 | diagnostics |
| Startup work that must finish before serving (warm-up, schema check) | `Check((services, cancellationToken) => ...)`; checks run after hosted services are constructed and startup options are validated, and before hosted services start. Work a hosted service's constructor depends on stays between `Build` and `Run`; changing that dependency is a separate decision. The verifier does not run checks, a test that starts the host does | composition |
| `AddHttpClient<T>`, `AddDbContext<T>` from two areas | TRP001 advisories on the client or `DbContextOptions`; one component owns it | diagnostics |
| `AddHealthChecks().AddCheck(...)` from several areas | works from each component; checks accumulate | — |
| Order-sensitive registrations (EF Core interceptors, mediator behaviors, `IStartupFilter`) | one callback, or callbacks of one kind with `Requires` between their components: components run in dependency order, and among those whose requirements have run, in ordinal full-type-name order, not `Program.cs` order. `Services` callbacks run inside `AddTroupe`, contributions later when the adapter applies them, so a contribution's registration follows every `Services` registration | composition |

## Application-owned builders

| Pattern | Target | Guide |
| --- | --- | --- |
| `UseWolverine(options => ...)` with handlers of several areas | `Contribute<WolverineOptions>(...)`; `builder.UseWolverine(options => builder.ApplyContributions(options))` | contributions |
| `AddMassTransit(x => { x.AddConsumer<...>(); })` | `Contribute<IBusRegistrationConfigurator>(x => x.AddConsumer<T>())`; `services.AddMassTransit(x => { builder.ApplyContributions(x); x.UsingX(...); })`. Several consumers raise TRP001 on `IConsumerRegistration`: declare `MultipleAllowed<IConsumerRegistration>()` | contributions |
| `AddMediatR(cfg => cfg.RegisterServicesFromAssembly(...))` across areas | `Contribute<MediatRServiceConfiguration>(...)` applied in `services.AddMediatR(cfg => builder.ApplyContributions(cfg))`; the handlers it scans afterwards are owned by `application` | contributions |
| Extensions on `IHostApplicationBuilder` (.NET Aspire `AddServiceDefaults`, `AddNpgsqlDataSource`, `EnrichNpgsqlDbContext`; templates' `builder.AddInfrastructureServices()`) | `Contribute<IHostApplicationBuilder>(host => host.AddX(...))`; `builder.ApplyContributions<IHostApplicationBuilder>(builder)` with the explicit type argument (the inferred `WebApplicationBuilder` leaves them unapplied, TRP008) | contributions |

Registrations made inside a contribution callback are owned by the contributor; registrations the adapter makes
itself are owned by `application`. Every contribution must be applied exactly once (TRP008).

## Host and pipeline

| Pattern | Target | Guide |
| --- | --- | --- |
| Configuration sources (`AddJsonFile`, Key Vault, `AddKeyVaultIfConfigured`) | stay in `Program.cs`, before `AddTroupe`; a source added later does not count for TRP005 | configuration |
| `UseDefaultServiceProvider`, `ConfigureContainer`, `UseServiceProviderFactory` | remove; Troupe enables `ValidateOnBuild` and `ValidateScopes` (TRP018) | diagnostics |
| Autofac, Lamar or another container | not supported; a gap | — |
| Logging, Serilog `UseSerilog`, `builder.Logging` | stay in `Program.cs` | — |
| `app.UseRouting()` | `Use("routing", application => application.UseRouting())` in the ingress component | aspnetcore |
| `app.UseX()` an area owns (authentication, CORS of an API) | `Use(stage, ...)` with the stage in `definition.Stages(...)` | aspnetcore |
| Middleware that must wrap everything (exception handler, HSTS, forwarded headers) | stays in `Program.cs` before `app.UseComposition()` | aspnetcore |
| Middleware after `UseRouting` that reads the endpoint (authorization, endpoint-metadata policies) | a stage after the routing stage, or in `Program.cs` after `app.UseComposition()`; before `UseComposition` it sees no endpoint | aspnetcore |
| `app.MapX()`, `MapGroup` | `Endpoints(endpoints => ...)`; while not migrated, mapped in `Program.cs` before `app.UseComposition()`, which records the endpoints for route checks | aspnetcore |
| `AddControllers()` + `MapControllers()` | one component owns both; mapping from two components maps every controller twice (TRP102). Controllers come from application parts, by default the whole entry assembly, whatever is composed | aspnetcore |
| Reflection-based endpoint scanning (`app.MapEndpoints(assembly)`) | one component maps it; it maps everything in the assembly, like controllers | aspnetcore |
| `Kestrel:Endpoints` entries | once the composition declares stages, middleware or endpoints, each entry must be requested with `KestrelEndpoint(name)` by a composed component, and each requested name configured (TRP103) | aspnetcore |
| `UseUrls`, `ASPNETCORE_URLS`, `--urls` | unchanged while `Kestrel:Endpoints` has no entries; Kestrel uses the entries instead when there are any | aspnetcore |
| TLS or client certificate setup of one listener | `KestrelEndpoint(name, configure)`, one owner per endpoint (TRP104) | aspnetcore |
| Environment-dependent pipeline (`if (app.Environment.IsDevelopment())`) | stays in `Program.cs`, or the component's `Use` callback reads `IHostEnvironment` from `application.ApplicationServices` | aspnetcore |
| Startup database migration or seeding after `Build` | stays in `Program.cs`, or a `Check` when no hosted service's constructor depends on it; with roles it runs in every process (see split readiness) | composition |
| Several Host projects, one `Program.cs` per service | one executable; profiles after the split-readiness analysis. Each host's `appsettings*.json`, user secrets ID, application name, content root, launch settings, Dockerfiles and orchestration references are merged or kept per profile deliberately | composition |

## Tests and tools

| Pattern | Target | Guide |
| --- | --- | --- |
| A test that builds the host | `CompositionVerifier.VerifyAsync<T>(() => App.CreateBuilder([]))`; `VerifyProfilesAsync` with profiles | testing |
| `WebApplicationFactory<Program>`, `ConfigureTestServices` | unchanged | testing |
| Test-only slices built from `AddX()` calls | `ComposeForTest<T>(root => root.Requires<XComponent>())` | testing |
| Replacing a service in tests | `TestServices`, `ReplaceForTest<T>` before `Build` (TRP019) | testing |
| `dotnet ef` and other tools that start the host | they run `Build` and its checks: provide the required configuration, or an `IDesignTimeDbContextFactory<T>` | — |
