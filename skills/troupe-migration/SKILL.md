---
name: troupe-migration
description: Migrate a .NET application's host composition to Corfedo Troupe, and analyze whether its parts can run as separate processes. Inventories Program.cs, AddX/MapX/UseX extension methods and Host projects, proposes components and reports before editing, moves one area at a time into IComponent classes, verifies with CompositionVerifier and startup tests, and adds roles and profiles only after a split-readiness analysis. Use when asked to adopt or migrate to Troupe, move registrations into components, untangle composition code or project references that exist only for host wiring, merge several Host projects into one executable with profiles, or check whether part of a .NET application can run on its own.
---

# Migrating to Troupe

A migration ends in components that run in one process. Roles and profiles are a separate, optional step for
applications whose parts must run on their own; most applications never take it. The tutorial for people is
[Migrating an existing application](https://github.com/corfedo/corfedo-troupe/blob/main/docs/guides/migrating.md);
the guides this skill refers to by name (composition, configuration, contributions, manifest, aspnetcore, testing,
diagnostics) are in `docs/guides/` of the Troupe repository at the tag of the version in use,
`https://github.com/corfedo/corfedo-troupe/tree/v<version>/docs/guides`.

The user approves the plan before you edit, and decides every behavior change. Troupe only knows whether a component is
composed in a process. It cannot see shared state, so the split-readiness analysis is advisory and based on reading
the code.

## 0. Classify the request

Decide which outcome the user asked for, and confirm it if unclear:

- **Split analysis only** ("can X run on its own?"): run phase 1 bounded to the parts in question, then phase 6. No
  migration preconditions apply, nothing is edited, and the result is the report.
- **Migration:** phases 1 to 5. Phases 6 and 7 only when the user asks for them afterwards.
- **Merging Host projects:** the migration first, then phases 6 and 7 for the parts the Host projects run.

Then:

- Find the Troupe version the application references or will reference. This skill describes 0.2.0
  (`AddTroupe<T>()`, `definition.Requires<T>()`, typed profiles). For another version, read that version's README and
  guides at its tag and follow them where they differ; the API changes before 1.0.
- Read the repository's agent instructions (`AGENTS.md`, `CLAUDE.md`, contribution guides). Their rules on branches,
  commits, tests and dependencies apply to every step below.
- For a migration, check the hard preconditions and report each one that fails before going further:
  - the host uses `WebApplication.CreateBuilder` or `Host.CreateApplicationBuilder` (an `IHostApplicationBuilder`);
    `Host.CreateDefaultBuilder` with a `Startup` class needs a hosting-model change first, which is its own task;
  - no third-party DI container (Autofac, Lamar, DryIoc through `UseServiceProviderFactory`);
  - no Native AOT publishing (`PublishAot`);
  - .NET 10 or later.

## 1. Inventory (read only)

In a large solution, inventory one Host project and the projects it composes at a time, and report the size of the
rest before reading it. Collect every composition site. Search, then read each hit:

- entry points: projects with `Sdk="Microsoft.NET.Sdk.Web"`, `<OutputType>Exe</OutputType>`, `Program.cs`,
  `Startup.cs`, worker templates; several Host projects per solution are candidates for profiles later;
- extension methods: `this IServiceCollection`, `this IHostApplicationBuilder`, `this WebApplicationBuilder`,
  `this IEndpointRouteBuilder`, `this IApplicationBuilder`, `this WebApplication`;
- registrations: `services.Add`, `TryAdd`, `Replace`, `RemoveAll`, `AddHostedService`, `AddHttpClient`, `AddDbContext`,
  `AddControllers`, `AddHealthChecks`, `AddAuthentication`, `AddMediatR`, `AddMassTransit`, `UseWolverine`;
- configuration: `GetSection`, `GetConnectionString`, `GetValue`, `configuration[`, `Configure<`, `BindConfiguration`,
  `Bind(`, configuration sources (`AddJsonFile`, `AddAzureKeyVault`, `AddEnvironmentVariables`);
- conditions: `IsDevelopment`, `IsEnvironment`, `if (` around registrations, feature flags read at startup;
- provider and host settings: `ConfigureContainer`, `UseServiceProviderFactory`, `UseDefaultServiceProvider`,
  `builder.Host.`, `builder.WebHost.`, `UseUrls`, and every entry in `Kestrel:Endpoints` in each `appsettings*.json`
  and deployment file;
- pipeline: every `app.Use*`, `app.Map*`, `MapControllers`, reflection-based endpoint scanning, and their order;
- per Host project: `appsettings*.json`, `UserSecretsId`, `ApplicationName` and content root, `launchSettings.json`,
  Dockerfiles, Compose or Kubernetes files, Aspire AppHost references, and which of them are in the repository;
- tests: those that start the host or send requests (`WebApplicationFactory`, integration tests), test-only `Add*`
  calls, service replacements.

Classify each site with [references/patterns.md](references/patterns.md): its Troupe target, or a gap. Group the sites
by area. An area is a product capability or an infrastructure concern with one owner, usually one project or folder.
A layered solution (Domain, Application, Infrastructure, Web) has layers, not areas: propose one component per existing
registration method, and say that this does not create split boundaries.

## 2. Proposal report, then stop

Write the report in the chat (or a file the user names), not in the repository:

1. **Preconditions** and their status.
2. **Inventory:** each composition site with file and line, what it registers, the sections it reads, its target or
   gap.
3. **Components:** name (`<Area>Component`), project and folder it lives in (the area's own project, not the Host),
   what moves into it, owned sections (`Options<T>`), other reads (`ReadsConfiguration`), its `Requires<T>()`, middleware
   stages, endpoints and listeners (`KestrelEndpoint`). Shared parts several areas need are components of their own.
4. **Requirement graph** and the definition: `definition.Requires<T>()` roots and `definition.Stages(...)`. Each
   `Requires<T>()` edge needs the requiring project to reference the required one. Where the existing references run
   the other way (Infrastructure implementing Application's interfaces), the definition or a Host component requires
   both instead. If the graph cannot be expressed with the existing references, say so and ask; do not add references
   against the solution's dependency direction.
5. **References to cut:** project references that exist only so one composition method can see another project's
   types, and the references that stay.
6. **Behavior changes** the migration would cause: registration order (see patterns), duplicate registrations that
   collapse to one, `ValidateOnBuild`/`ValidateScopes` failures, sections that become required, listener checks,
   middleware order.
7. **Gaps:** what has no Troupe target and stays in `Program.cs`, with the reason.
8. **Tests:** the existing tests that start the application or send requests per area, and the ones to add where an
   area has none.
9. **Order of steps:** shared and leaf components first, one area per step.

Stop and ask the user to approve or change the plan. Ask specifically about every behavior change and every gap. Do not
edit before the approval.

## 3. Prepare the host (one change)

- Before editing, run the build and the tests that start the application, and record the results as the baseline.
- Add the packages the plan names (`Corfedo.Troupe` in the projects with components and the Host,
  `Corfedo.Troupe.AspNetCore` where components declare middleware, endpoints or listeners, `Corfedo.Troupe.Testing` in
  the test project), following the repository's package management (central versions, lock files).
- Move builder creation into a method tests can call (`<App>Application.CreateBuilder(args)`) if there is none.
- Add the definition and `builder.AddTroupe<TDefinition>()` after every configuration source; add
  `app.UseComposition()` for web hosts, after the endpoints `Program.cs` still maps. Middleware keeps its position
  relative to routing: what ran before `UseRouting` stays before `UseComposition`; what ran after it (authorization,
  endpoint-metadata policies) moves into a stage after the routing stage or stays after `UseComposition`. The definition needs at least one requirement (TRP012), so this change also moves the first component.
- Once the composition declares stages, middleware or endpoints, every configured `Kestrel:Endpoints` entry must be
  requested (TRP103): request each existing entry from the component that serves it in this change.
- Remove `UseDefaultServiceProvider`, `ConfigureContainer` and `UseServiceProviderFactory` calls.
- Add one composition test:
  `Assert.True((await CompositionVerifier.VerifyAsync<TDefinition>(() => App.CreateBuilder([]))).Succeeded, ...)`,
  with the content root, environment and configuration the test environment provides.
- Build and run the composition test and the baseline tests.

## 4. Move one area per step

For each component in the approved order:

1. Create the component in the area's project. Move the registrations, the configuration reads, the middleware and the
   endpoints of that area into it, translated with [references/patterns.md](references/patterns.md).
2. Delete the moved code from `Program.cs` and from the old extension methods in the same step. Remove calls from
   not-yet-migrated extensions into the migrated area: a leftover `Configure<T>` binds the section twice without any
   diagnostic. Delete extension methods that are left empty; keep public ones other applications use, and call them
   from the component.
3. Add the `Requires<T>()` edges the approved graph lists.
4. Build, run the composition test, the tests that start the application and the area's tests. The verifier does not
   start the host: startup checks, hosted services and middleware in `Program.cs` are covered only by the tests that
   start it. Then read the manifest: no new TRP001 advisory you have not explained, the moved services owned by the
   component, nothing of the area still owned by `application`
   (`manifest.Services.Where(s => s.Active && s.Owner.Kind == OwnerKind.Application)`) except registrations an adapter
   makes after its contributions run (such as handlers MediatR scans), which the plan lists with their expected owner.
5. Report the step in this form, so the migration can stop and resume after any step:

   ```text
   Step: <component>
   Moved: <registrations, sections, middleware, endpoints>
   Still in Program.cs: <what remains for later steps>
   Checks: <command> — <result>, compared with the baseline
   Advisories: <code> — <resolution>
   Next: <component> | Blocked: <decision needed>
   ```

Stop and ask when a step would change behavior that the plan did not list, when a test fails for a reason other than
your change, or when the code contradicts the plan. Fix a failure you caused; do not weaken a test or a check to pass.

## 5. Finish the migration

The migration is complete when:

- `Program.cs` holds builder creation, configuration sources, `AddTroupe`, the adapters (`ApplyContributions`),
  remaining application endpoints and pre-routing middleware before `UseComposition`, middleware that must follow
  routing after it, and `Run`;
- the registrations owned by `application` are the host's defaults and those the adapters make;
- the composition test and the tests that start the application pass, and every remaining advisory is explained or
  declared with `MultipleAllowed<T>()`.

Report the final state, the checks run and their results against the baseline, and the gaps left in `Program.cs`.

## 6. Split-readiness analysis

Follow [references/split-readiness.md](references/split-readiness.md). It runs on a migrated application, or on the
code of one that is not. The output is a verdict per candidate boundary: ready, needs a contract, keep together, or
inconclusive, each with code locations. Stop and let the user choose the boundaries. Choosing a boundary approves the
intended split; it does not make it ready.

## 7. Roles and profiles (only after the user chose them)

- A boundary becomes separate profiles only when its verdict is ready, or when every contract it needs exists and is
  verified. Contracts (durable messages, remote calls, shared stores, single-instance leases) are their own work that
  the user approves and that comes first; propose them, and do not declare split profiles as runnable before they exist.
  Inconclusive findings are resolved before, or the user accepts them explicitly.
- One `IRole` per chosen part, `[Role("name")]` with the name operators will type, requiring the existing components.
  Components that every process needs, such as telemetry, stay `definition.Requires<T>()`.
- Profiles for each supported process, plus one that runs every role (`combined`) for local runs and single-process
  deployments. `Profile` has no default: set it in `appsettings.Development.json` and in every deployment (TRP013).
- Merging Host projects: settle, per Host project, its configuration files, user secrets ID, application name and
  content root, launch settings, Dockerfiles and orchestration references from the inventory. Files in the repository
  change in the approved plan; list the ones outside it, with the change each needs (`--profile`, per-process
  `Kestrel:Endpoints`, sections only some processes read), for the user.
- Verify every profile with `CompositionVerifier.VerifyProfilesAsync<T>(profile => App.CreateBuilder(["--profile", profile.Name]))`,
  and start each profile in a test. The verifier catches constructor dependencies on components a profile does not
  compose. Check by reading what it does not: dependencies inside service factories, `IEnumerable<T>` consumers that
  become empty, minimal API parameters inferred as request bodies (the service is missing, so `POST` binds the type
  from the body).
