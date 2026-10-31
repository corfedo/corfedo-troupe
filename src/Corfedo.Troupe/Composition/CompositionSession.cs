using System.Collections.Frozen;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe;

internal sealed class CompositionSession
{
    private IConfiguration? configuration;
    private ServiceLedger? ledger;
    private ICompositionRule[] rules;
    private readonly IReadOnlyList<ContributorEntry> contributors;
    private readonly IReadOnlyList<ConfigurationRead> reads;
    private readonly List<PendingContribution> contributions = [];
    private readonly List<StartupCheck> checks = [];
    private readonly Dictionary<Type, Action<IServiceProvider>> optionResolvers = [];
    private readonly HashSet<Type> appliedTargets = [];
    private readonly HashSet<Type> multiple = [];
    private readonly HashSet<(Type Type, object? Key)> exclusive = [];
    private readonly List<CompositionDiagnostic> failures = [];
    private ICompositionIntegration? integration;
    private object? failedIntegrationSnapshot;
    private CompositionException? failure;

    internal CompositionSession(IHostApplicationBuilder builder, DefinitionBuilder definition,
        ProfileIdentity? profile, RoleIdentity[] roles, ContributorBuilder[] nodes)
    {
        DefinitionType = definition.DefinitionType;
        configuration = builder.Configuration;
        ledger = new(builder.Services);
        rules = definition.Rules.ToArray();
        Runtime = new(profile, Array.AsReadOnly(roles), nodes.Where(n => n.Owner.Type is not null).Select(n => n.Owner.Type!).ToFrozenSet());
        contributors = Array.AsReadOnly(nodes.Select(n => new ContributorEntry(n.Owner,
            Array.AsReadOnly(n.Requirements.Keys.Select(t => nodes.Single(x => x.Owner.Type == t).Owner)
                .OrderBy(o => o.Name, StringComparer.Ordinal).ToArray()))).ToArray());
        reads = Array.AsReadOnly(nodes.SelectMany(n => n.Configuration).ToArray());
        ledger.Synchronize();
    }

    internal Type? DefinitionType { get; }
    internal RuntimeComposition Runtime { get; }
    internal CompositionPhase Phase { get; private set; } = CompositionPhase.Composing;
    internal IConfiguration Configuration => configuration ?? throw new InvalidOperationException("Registration is sealed.");
    internal bool AllowsMultiple(Type type) => multiple.Contains(type);
    internal bool IsExclusive(Type type, object? key) => exclusive.Contains((type, key));

    internal void AttachIntegration(ICompositionIntegration value)
    { RequirePhase(CompositionPhase.Composing); integration = value; }
    internal T? TryIntegration<T>() where T : class, ICompositionIntegration => integration as T;
    internal T Integration<T>() where T : class, ICompositionIntegration => integration as T ??
        throw Fail(DiagnosticCodes.InvalidPhase, "This composition has no web integration.", ServiceLedger.Application);
    internal T? IntegrationSnapshot<T>() where T : class =>
        failedIntegrationSnapshot as T ?? integration?.Snapshot() as T ?? Runtime.IntegrationSnapshot<T>();

    internal void ValidateDeclarations()
    {
        foreach (var duplicate in reads.Where(r => r.OptionsType is not null).GroupBy(r => r.OptionsType).Where(g => g.Count() > 1))
            throw Fail("TRP007", $"Options {duplicate.Key!.FullName} already has an owned section; a second declaration is not supported.",
                duplicate.Select(r => r.Owner).Distinct().ToArray());
    }
    internal void Execute(CompositionOwner owner, Action<CompositionSession, CompositionOwner> action) =>
        Invoke(owner, "registration", () => action(this, owner));
    internal void CompleteComposition() { RequirePhase(CompositionPhase.Composing); Phase = CompositionPhase.Configuring; }

    internal void ConfigureServices(CompositionOwner owner, Action<IServiceCollection> callback)
    {
        RequirePhase(CompositionPhase.Composing);
        Invoke(owner, "services", () => ledger!.Configure(owner, callback));
    }
    internal void RegisterFrameworkServices(IServiceCollection services)
    {
        RequirePhase(CompositionPhase.Composing);
        services.AddSingleton(this);
        services.AddSingleton<IComposition>(Runtime);
        services.AddSingleton<IHostedService, CompositionLifecycle>();
    }
    internal void RegisterOptions(Type type, Action<IServiceProvider> resolve)
    { RequirePhase(CompositionPhase.Composing); optionResolvers.Add(type, resolve); }
    internal void AddCheck(CompositionOwner owner, Func<IServiceProvider, CancellationToken, Task> callback)
    { RequirePhase(CompositionPhase.Composing); checks.Add(new(owner, callback)); }
    internal void AddContribution(CompositionOwner owner, Type target, Action<object> callback)
    { RequirePhase(CompositionPhase.Composing); contributions.Add(new(owner, target, callback)); }
    internal void AllowMultiple(Type type) { RequirePhase(CompositionPhase.Composing); multiple.Add(type); }
    internal void RequireExclusive(Type type, object? key) { RequirePhase(CompositionPhase.Composing); exclusive.Add((type, key)); }

    internal void ApplyContributions<T>(T target)
    {
        RequirePhase(CompositionPhase.Configuring);
        if (!appliedTargets.Add(typeof(T))) throw Fail("TRP008", $"Contributions to {typeof(T)} have already been applied.",
            contributions.Where(c => c.Target == typeof(T)).Select(c => c.Owner).Distinct().ToArray());
        foreach (var contribution in contributions.Where(c => c.Target == typeof(T)))
        {
            var callback = contribution.Callback!;
            Invoke(contribution.Owner, $"contribution to {typeof(T).FullName}", () => ledger!.Attribute(contribution.Owner, () => callback(target!)));
            contribution.Applied = true;
            contribution.Callback = null;
        }
        Validate();
    }
    internal static CompositionOwner TestOwner(string name) => new(OwnerKind.Test, $"test:{name}");
    // Privileged overrides (the profile verifier's) may remove any owner's registrations; ordinary ones are checked like contributors.
    internal void ApplyTestOverrides(string name, Action<IServiceCollection> callback, bool privileged)
    {
        RequirePhase(CompositionPhase.Configuring, CompositionPhase.Finalized);
        var owner = TestOwner(name);
        RequireWritableServices(owner);
        Invoke(owner, "test overrides", () => ledger!.Configure(owner, callback, privileged));
    }
    internal void ReplaceForTest(string name, ServiceDescriptor replacement, Type? ownerType)
    {
        RequirePhase(CompositionPhase.Configuring, CompositionPhase.Finalized);
        var owner = TestOwner(name);
        RequireWritableServices(owner);
        Invoke(owner, "test replacement", () => ledger!.Replace(owner, replacement, ownerType));
    }
    private void RequireWritableServices(CompositionOwner owner)
    {
        if (ledger!.IsReadOnly) throw Fail(DiagnosticCodes.InvalidPhase,
            $"{owner.Name}: the host is built and its services are read-only; apply test overrides before Build.", owner);
    }
    internal void ResolveDeclaredOptions(IServiceProvider services, CancellationToken cancellationToken)
    {
        RequirePhase(CompositionPhase.Finalized);
        foreach (var resolve in optionResolvers.Values)
        { cancellationToken.ThrowIfCancellationRequested(); resolve(services); }
    }

    internal CompositionManifest FinalizeComposition()
    {
        EnsureUsable();
        if (Phase is CompositionPhase.Starting or CompositionPhase.Started) return Runtime.Manifest;
        RequirePhase(CompositionPhase.Configuring, CompositionPhase.Finalized, CompositionPhase.PipelineApplied);
        var manifest = Validate(finalizing: true);
        if (Phase == CompositionPhase.Configuring) Phase = CompositionPhase.Finalized;
        return manifest;
    }
    internal void BeginPipeline()
    {
        RequireFinalized();
        RequirePhase(CompositionPhase.Finalized);
        FinalizeComposition();
        Phase = CompositionPhase.ApplyingPipeline;
    }
    internal void RunPipelineCallback(CompositionOwner owner, Action callback)
    { RequirePhase(CompositionPhase.ApplyingPipeline); Invoke(owner, "pipeline", callback); }
    internal void CompletePipeline()
    {
        RequirePhase(CompositionPhase.ApplyingPipeline);
        Validate();
        Phase = CompositionPhase.PipelineApplied;
        integration?.ReleaseCallbacks();
    }
    internal void RequireConfiguration() => RequirePhase(CompositionPhase.Configuring);

    internal CompositionManifest Validate(bool finalizing = false)
    {
        EnsureUsable();
        if (Phase is CompositionPhase.Starting or CompositionPhase.Started) return Runtime.Manifest;
        try { ledger!.Synchronize(); }
        catch (CompositionException exception) { throw RecordFailure(exception.Diagnostics, exception); }
        CompositionManifest manifest;
        try { manifest = EvaluateRules(finalizing); }
        catch (CompositionException exception) { throw RecordFailure(exception.Diagnostics, exception); }
        catch (Exception exception)
        { throw Fail(DiagnosticCodes.CallbackFailure, "Application composition rule failed.", [ServiceLedger.Application], exception); }
        var errors = manifest.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) throw RecordFailure(errors, manifest: manifest);
        Runtime.Publish(manifest, integration?.Snapshot());
        return manifest;
    }

    private CompositionManifest CreateSnapshot()
    {
        var services = ledger is not null ? Array.AsReadOnly(ledger.Registrations()) : Runtime.Manifest.Services;
        var mutations = ledger is not null ? Array.AsReadOnly(ledger.Mutations()) : Runtime.Manifest.ServiceMutations;
        return new(Runtime.Profile, Runtime.Roles, contributors, services, mutations, reads,
            Array.AsReadOnly(contributions.Select(c => new ContributionEntry(c.Owner, c.Target, c.Applied)).ToArray()),
            Array.AsReadOnly(failures.Distinct(DiagnosticComparer.Instance).ToArray()));
    }
    private CompositionManifest EvaluateRules(bool finalizing)
    {
        var manifest = CreateSnapshot();
        var diagnostics = CoreRules.Evaluate(this, manifest).ToList();
        if (integration is not null) diagnostics.AddRange(integration.Evaluate(manifest));
        if (finalizing)
            diagnostics.AddRange(contributions.Where(c => !c.Applied).Select(c => new CompositionDiagnostic("TRP008", DiagnosticSeverity.Error,
                $"Unconsumed contribution to {c.Target.FullName} from {c.Owner.Name}.", Array.AsReadOnly(new[] { c.Owner }))));
        // Application rules independently inspect the same framework-diagnostic snapshot.
        manifest = manifest.WithDiagnostics(diagnostics);
        var applicationDiagnostics = rules.SelectMany(rule => rule.Evaluate(manifest)).ToArray();
        return manifest.WithDiagnostics(diagnostics.Concat(applicationDiagnostics));
    }

    internal CompositionManifest FailureSnapshot(Exception? exception = null)
    {
        if (failure is not null) return failure.Manifest!;
        if (exception is not null)
            return RecordFailure([new(DiagnosticCodes.CallbackFailure, DiagnosticSeverity.Error,
                "Host verification failed; see the original exception.", Array.AsReadOnly(new[] { ServiceLedger.Application }))], exception).Manifest!;
        return CreateSnapshot();
    }
    internal CompositionException Fail(string code, string message, params CompositionOwner[] owners) =>
        Fail(code, message, owners, null);
    private CompositionException Fail(string code, string message, CompositionOwner[] owners, Exception? innerException) =>
        RecordFailure([new(code, DiagnosticSeverity.Error, message, Array.AsReadOnly(owners))], innerException);
    private CompositionException RecordFailure(IEnumerable<CompositionDiagnostic> diagnostics, Exception? innerException = null, CompositionManifest? manifest = null)
    {
        if (failure is not null) return failure;
        failures.AddRange(diagnostics);
        if (manifest is null) ledger?.Observe();
        if (ledger is not null) failures.AddRange(ledger.TakeViolations());
        manifest = (manifest ?? CreateSnapshot()).WithDiagnostics((manifest?.Diagnostics ?? []).Concat(failures));
        failedIntegrationSnapshot = integration?.Snapshot();
        failure = new(manifest.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray(), manifest, innerException);
        Phase = CompositionPhase.Failed;
        ReleaseRegistrationState();
        foreach (var check in checks) check.Callback = null;
        checks.Clear();
        return failure;
    }
    private void Invoke(CompositionOwner owner, string operation, Action callback)
    {
        EnsureUsable();
        try { callback(); }
        catch (CompositionException exception)
        {
            throw RecordFailure(exception.Diagnostics.Select(d => d.Owners.Count == 0 ? d with { Owners = Array.AsReadOnly(new[] { owner }) } : d), exception);
        }
        catch (Exception exception)
        { throw Fail(DiagnosticCodes.CallbackFailure, $"{owner.Name}: {operation} callback failed.", [owner], exception); }
    }
    private void RequirePhase(params CompositionPhase[] permitted)
    {
        EnsureUsable();
        if (!permitted.Contains(Phase)) throw Fail(DiagnosticCodes.InvalidPhase,
            $"Composition is {Phase}; this operation requires {string.Join(" or ", permitted)}.", ServiceLedger.Application);
    }
    private void EnsureUsable() { if (failure is not null) throw failure; }

    // Pipeline application and startup revalidate a finalized composition; they do not finalize it themselves.
    private void RequireFinalized()
    {
        EnsureUsable();
        if (Phase == CompositionPhase.Configuring) throw Fail(DiagnosticCodes.Unfinalized,
            "Composition is not finalized. Call FinalizeComposition on the builder before Build.", ServiceLedger.Application);
    }

    internal CompositionManifest ValidateServingComposition()
    {
        RequireFinalized();
        var manifest = FinalizeComposition();
        if (integration is { PipelineOwners.Count: > 0 } && Phase != CompositionPhase.PipelineApplied)
            throw Fail(DiagnosticCodes.UnappliedPipeline, "Recorded middleware or endpoints were not applied. Call UseComposition on a web application before Start.", integration.PipelineOwners.ToArray());
        return manifest;
    }

    internal async Task RunChecksAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ValidateServingComposition();
        RequirePhase(CompositionPhase.Finalized, CompositionPhase.PipelineApplied);
        Phase = CompositionPhase.Starting;
        ReleaseRegistrationState();
        var pendingChecks = checks.ToArray(); checks.Clear();
        var currentOwner = ServiceLedger.Application;
        try
        {
            foreach (var check in pendingChecks)
            {
                currentOwner = check.Owner;
                cancellationToken.ThrowIfCancellationRequested();
                try { await check.Callback!(services, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    RecordFailure([new(DiagnosticCodes.CallbackFailure, DiagnosticSeverity.Error,
                        $"{check.Owner.Name}: startup check failed.", Array.AsReadOnly(new[] { check.Owner }))], exception);
                    throw new CompositionCheckException(check.Owner, exception);
                }
                finally { check.Callback = null; }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            RecordFailure([new(DiagnosticCodes.CallbackFailure, DiagnosticSeverity.Error,
                $"{currentOwner.Name}: startup checks were canceled.", Array.AsReadOnly(new[] { currentOwner }))], exception);
            throw;
        }
        finally { foreach (var check in pendingChecks) check.Callback = null; }
    }
    internal void CompleteStart() { RequirePhase(CompositionPhase.Starting); Phase = CompositionPhase.Started; }
    private void ReleaseRegistrationState()
    {
        ledger = null; configuration = null; rules = [];
        optionResolvers.Clear();
        foreach (var contribution in contributions) contribution.Callback = null;
        integration?.ReleaseCallbacks(); integration = null;
    }

    internal static CompositionSession Get(IHostApplicationBuilder builder) => TryGet(builder) ??
        throw new InvalidOperationException("Compose a definition first.");
    internal static CompositionSession? TryGet(IHostApplicationBuilder builder) =>
        builder.Properties.TryGetValue(typeof(CompositionSession), out var value) ? value as CompositionSession : null;
}

internal sealed class PendingContribution(CompositionOwner owner, Type target, Action<object> callback)
{
    internal CompositionOwner Owner { get; } = owner;
    internal Type Target { get; } = target;
    internal Action<object>? Callback = callback;
    internal bool Applied;
}
