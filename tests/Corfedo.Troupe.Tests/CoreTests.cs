using Corfedo.Troupe;
using Corfedo.Troupe.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Corfedo.Troupe.Tests;

public sealed class CoreTests
{
    public CoreTests() { A.Declare = _ => { }; B.Declare = _ => { }; }
    internal static HostApplicationBuilder Builder(string roles = "a,b")
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration["Roles"] = roles;
        return builder;
    }

    [Theory]
    [InlineData("b,a,a")]
    [InlineData("a,b")]
    public void SelectionIsASetAndGraphUsesReadyNodeOrdinalTies(string selection)
    {
        using var host = ComposeGraph(selection, out var manifest);
        var names = manifest.Contributors.Select(n => n.Owner.Name).ToArray();
        Assert.Equal([typeof(Store).FullName!, typeof(Branch).FullName!, "a", "b"], names);
        Assert.Equal(1, manifest.Contributors.Count(c => c.Owner.Type == typeof(Store)));
        Assert.True(host.Services.GetRequiredService<IComposition>().Contains<A>());
    }
    private static IHost ComposeGraph(string selection, out CompositionManifest manifest)
    {
        A.Declare = r => r.Requires<Store>(); B.Declare = r => r.Requires<Branch>().Requires<Store>();
        var builder = Builder(selection); manifest = builder.AddTroupe<Definition>(); return builder.Build();
    }
    [Fact]
    public void GraphIsFullyValidatedBeforeServiceCallbacksAndUnselectedRolesAreNotDeclared()
    {
        var callbacks = 0;
        A.Declare = r => { r.Services(_ => callbacks++); r.Requires<Cycle1>(); };
        B.Declare = _ => throw new Exception("unselected declaration");
        var error = Assert.Throws<CompositionException>(() => Builder("a").AddTroupe<Definition>());
        Assert.Contains($"{typeof(Cycle1).FullName} -> {typeof(Cycle2).FullName} -> {typeof(Cycle1).FullName}", error.Message);
        Assert.Equal(0, callbacks);
        A.Declare = r => r.Services(_ => callbacks++);
        Builder("a").AddTroupe<Definition>();
        Assert.Equal(1, callbacks);
    }
    [Fact]
    public void DeclaredProfilesAreSelectedByNameAndRecorded()
    {
        var builder = Builder(""); builder.Configuration["Profile"] = "both";
        var manifest = builder.AddTroupe<ProfileDefinition>();
        Assert.Equal("both", manifest.Profile!.Name); Assert.Equal(["a", "b"], manifest.Roles.Select(r => r.Name));
        using var host = builder.Build();
        Assert.Equal("both", host.Services.GetRequiredService<IComposition>().Profile!.Name);
        Assert.Equal(["both"], CompositionExtensions.DeclaredProfiles<ProfileDefinition>().Select(p => p.Name));
        Assert.Null(Builder("a").AddTroupe<Definition>().Profile);
    }
    [Theory]
    [InlineData("", "a,b", "not Roles. Declared profiles: both.")]
    [InlineData("", "", "Select a declared profile using Profile configuration. Declared profiles: both.")]
    [InlineData("unknown", "", "Unknown profile unknown. Declared profiles: both.")]
    public void ExactProfilesRejectInvalidSelections(string profile, string roles, string message)
    {
        var builder = Builder(roles); builder.Configuration["Profile"] = profile;
        var error = Assert.Throws<CompositionException>(() => builder.AddTroupe<ProfileDefinition>());
        Assert.Contains(error.Diagnostics, d => d.Code == "TRP013" && d.Message.Contains(message));
    }
    [Fact]
    public void ProfileSelectionRequiresDeclaredProfiles()
    {
        var builder = Builder("a"); builder.Configuration["Profile"] = "a";
        Assert.Contains(Assert.Throws<CompositionException>(() => builder.AddTroupe<Definition>()).Diagnostics, d => d.Code == "TRP013");
    }
    [Fact]
    public void DuplicateRoleAndProfileIdentitiesAreRejected()
    {
        Assert.Throws<CompositionException>(() => Builder().AddTroupe<DuplicateRoleDefinition>());
        Assert.Throws<CompositionException>(() => Builder().AddTroupe<DuplicateNameDefinition>());
        Assert.Throws<CompositionException>(() => Builder().AddTroupe<MissingNameDefinition>());
        Assert.Contains("TRP011", Assert.Throws<CompositionException>(() => Builder().AddTroupe<EmptyProfileDefinition>()).Message);
        Assert.Contains("TRP011", Assert.Throws<CompositionException>(() => Builder().AddTroupe<DuplicateProfileDefinition>()).Message);
    }
    [Fact]
    public void ProfilesRegisterTheirRolesAndShareThemAcrossProfiles()
    {
        var profiles = CompositionExtensions.DeclaredProfiles<SharedRoleProfileDefinition>();
        Assert.Equal([["a"], ["a", "b"]], profiles.Select(p => p.Roles.ToArray()));
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration["Profile"] = "both";
        Assert.Equal(["a", "b"], builder.AddTroupe<SharedRoleProfileDefinition>().Roles.Select(r => r.Name));
    }
    [Fact]
    public void TracksEveryCollectionMutationAndRepeatedDescriptorOccurrences()
    {
        A.Declare = r => r.Services(s =>
        {
            var descriptor = ServiceDescriptor.Singleton<Foo, Foo>();
            s.Add(descriptor); s.Add(descriptor);
            s.Insert(s.Count - 1, ServiceDescriptor.Transient<Bar, Bar>());
            s[s.Count - 2] = ServiceDescriptor.Singleton<Bar, Bar>();
            s.RemoveAt(s.Count - 1); s.Remove(descriptor); s.Add(descriptor); s.Remove(descriptor);
        });
        var manifest = Builder("a").AddTroupe<Definition>();
        var foo = manifest.Services.Where(s => s.ServiceType == typeof(Foo)).ToArray();
        Assert.Equal(3, foo.Length);
        Assert.Equal(3, foo.Select(s => s.Id).Distinct().Count());
        Assert.All(foo, s => Assert.False(s.Active));
        Assert.Contains(manifest.Services, s => s.ReplacedBy is not null && s.RemovedBy!.Name == "a");
        Assert.Contains(manifest.ServiceMutations, m => m.Kind == ServiceMutationKind.Insert);
        Assert.Contains(manifest.ServiceMutations, m => m.Kind == ServiceMutationKind.Replace);
        Assert.Contains(manifest.ServiceMutations, m => m.Kind == ServiceMutationKind.RemoveAt);
        Assert.Contains(manifest.ServiceMutations, m => m.Kind == ServiceMutationKind.Remove);
    }
    [Theory]
    [InlineData("Remove")]
    [InlineData("RemoveAt")]
    [InlineData("Replace")]
    [InlineData("Clear")]
    [InlineData("RemoveAdd")]
    public void ForeignMutationsAreRejectedBeforeCollectionChanges(string operation)
    {
        A.Declare = r => r.Services(s => s.AddSingleton<Foo>());
        B.Declare = r => r.Services(s =>
        {
            var descriptor = s.Single(d => d.ServiceType == typeof(Foo));
            switch (operation)
            {
                case "Remove": s.Remove(descriptor); break;
                case "RemoveAt": s.RemoveAt(s.IndexOf(descriptor)); break;
                case "Replace": s[s.IndexOf(descriptor)] = ServiceDescriptor.Singleton<Foo, Foo>(); break;
                case "Clear": s.Clear(); break;
                case "RemoveAdd": s.Remove(descriptor); s.Add(descriptor); break;
            }
        });
        var builder = Builder();
        var error = Assert.Throws<CompositionException>(() => builder.AddTroupe<Definition>());
        Assert.Contains("TRP003", error.Message);
        Assert.Single(builder.Services, s => s.ServiceType == typeof(Foo));
        Assert.Contains(error.Manifest!.ServiceMutations, m => !m.Accepted && m.Owner.Name == "b");
    }
    [Fact]
    public void BaselineServicesHaveApplicationOwnershipAndCannotBeRemoved()
    {
        var builder = Builder("a"); builder.Services.AddSingleton<Foo>();
        A.Declare = r => r.Services(s => s.RemoveAll<Foo>());
        var exception = Assert.Throws<CompositionException>(() => builder.AddTroupe<Definition>());
        Assert.Contains("application", exception.Message);
    }
    [Theory]
    [InlineData(false, "a")]
    [InlineData(true, "b")]
    public void TryAddOwnershipDependsOnCanonicalOrderAndSuppressedAttemptsAreNotRegistrations(bool reverse, string expected)
    {
        A.Declare = r => { if (reverse) r.Requires<FirstB>(); r.Services(s => s.TryAddSingleton<Foo>()); };
        B.Declare = r => r.Services(s => s.TryAddSingleton<Foo>());
        var manifest = Builder().AddTroupe<Definition>();
        var owner = Assert.Single(manifest.Services, s => s.ServiceType == typeof(Foo)).Owner;
        Assert.Equal(reverse ? typeof(FirstB).FullName : expected, owner.Name);
        Assert.DoesNotContain(manifest.Diagnostics, d => d.Code == "TRP001");
    }
    [Fact]
    public void DuplicateServicesAreAdvisoryKeyedSlotsAreDistinctAndExclusivityIsExplicit()
    {
        A.Declare = r => r.Services(s => { s.AddKeyedSingleton<Foo>("x"); s.AddSingleton<Bar>(); });
        B.Declare = r => r.Services(s => { s.AddKeyedSingleton<Foo>("y"); s.AddSingleton<Bar>(); });
        var manifest = Builder().AddTroupe<Definition>();
        Assert.Single(manifest.Diagnostics, d => d.Code == "TRP001");
        A.Declare = r => { r.ExclusiveService<Foo>("x"); r.Services(s => s.AddKeyedSingleton<Foo>("x")); };
        B.Declare = r => r.Services(s => s.AddKeyedSingleton<Foo>("x"));
        Assert.Contains("TRP004", Assert.Throws<CompositionException>(() => Builder().AddTroupe<Definition>()).Message);
        A.Declare = r => { r.MultipleAllowed<Bar>(); r.Services(s => s.AddSingleton<Bar>()); };
        B.Declare = r => r.Services(s => s.AddSingleton<Bar>());
        Assert.DoesNotContain(Builder().AddTroupe<Definition>().Diagnostics, d => d.Code == "TRP001");
    }
    [Fact]
    public void HostedIdentityRuleDistinguishesKnownDuplicatesDistinctServicesAndUnknownFactories()
    {
        A.Declare = r => r.Services(s => s.AddSingleton<IHostedService, Worker>());
        B.Declare = r => r.Services(s => s.AddSingleton<IHostedService, Worker>());
        Assert.Contains("TRP002", Assert.Throws<CompositionException>(() => Builder().AddTroupe<Definition>()).Message);
        B.Declare = r => r.Services(s => s.AddSingleton<IHostedService, OtherWorker>());
        Assert.DoesNotContain(Builder().AddTroupe<Definition>().Diagnostics, d => d.Severity == DiagnosticSeverity.Error || d.Code == "TRP001");
        var calls = 0;
        B.Declare = r => r.Services(s => s.AddSingleton<IHostedService>(_ => { calls++; return new Worker(); }));
        Assert.Contains(Builder().AddTroupe<Definition>().Diagnostics, d => d.Code == "TRP006");
        Assert.Equal(0, calls);
        A.Declare = r => r.Services(s => s.AddHostedService<Worker>());
        B.Declare = r => r.Services(s => s.AddHostedService<Worker>());
        Assert.Single(Builder().AddTroupe<Definition>().Services, s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(Worker));
    }
    [Fact]
    public void TypedHttpClientRepeatsRemainAdvisory()
    {
        // AddHttpClient comes from the ASP.NET shared framework in this test assembly.
        A.Declare = r => r.Services(s => s.AddHttpClient<Client>());
        B.Declare = r => r.Services(s => s.AddHttpClient<Client>());
        var manifest = Builder().AddTroupe<Definition>();
        Assert.DoesNotContain(manifest.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(manifest.Diagnostics, d => d.Code == "TRP001" && d.Message.Contains(nameof(Client)));
    }
    [Fact]
    public void ContributionsAreOrderedAppliedOnceAndRequiredAtBuild()
    {
        A.Declare = r => r.Contribute<List<string>>(b => b.Add("a"));
        B.Declare = r => r.Contribute<List<string>>(b => b.Add("b"));
        var builder = Builder("b,a"); builder.AddTroupe<Definition>();
        var target = new List<string>(); builder.ApplyContributions(target);
        Assert.Equal(["a", "b"], target);
        Assert.Throws<CompositionException>(() => builder.ApplyContributions(new List<string>()));
        var pending = Builder(); pending.AddTroupe<Definition>();
        Assert.Contains("TRP008", Assert.Throws<CompositionException>(() => pending.Build()).Message);
        var duplicate = Builder(); duplicate.AddTroupe<Definition>(); duplicate.ApplyContributions(new List<string>());
        Assert.Throws<CompositionException>(() => duplicate.ApplyContributions(new List<string>()));
    }
    [Fact]
    public void ContributionsOwnRegistrationsTheyAddAndAdapterRegistrationsStayApplicationOwned()
    {
        B.Declare = r =>
        {
            r.Services(s => s.AddSingleton<Bar>());
            r.Contribute<ServicesTarget>(t => { t.Services.AddSingleton<Foo>(); t.Services.RemoveAll<Bar>(); });
        };
        var builder = Builder("b"); builder.AddTroupe<Definition>();
        builder.Services.AddSingleton<Client>();
        builder.ApplyContributions(new ServicesTarget(builder.Services));
        builder.Services.AddSingleton<OtherWorker>();
        var manifest = builder.FinalizeForTest();
        var role = new CompositionOwner(OwnerKind.Role, "b", typeof(B));
        var foo = Assert.Single(manifest.Services, s => s.ServiceType == typeof(Foo));
        Assert.Equal(role, foo.Owner); Assert.True(foo.Active);
        Assert.Contains(manifest.ServiceMutations, m => m.Occurrence == foo.Id && m.Kind == ServiceMutationKind.Add && m.Owner == role);
        Assert.Equal(role, Assert.Single(manifest.Services, s => s.ServiceType == typeof(Bar)).RemovedBy);
        Assert.Equal(ServiceLedgerApplication, Assert.Single(manifest.Services, s => s.ServiceType == typeof(Client)).Owner);
        Assert.Equal(ServiceLedgerApplication, Assert.Single(manifest.Services, s => s.ServiceType == typeof(OtherWorker)).Owner);
    }
    [Fact]
    public void ContributionsCannotRemoveAnotherOwnersRegistration()
    {
        A.Declare = r => r.Contribute<ServicesTarget>(t => t.Services.RemoveAll<Foo>());
        var builder = Builder("a"); builder.AddTroupe<Definition>();
        builder.Services.AddSingleton<Foo>();
        var error = Assert.Throws<CompositionException>(() => builder.ApplyContributions(new ServicesTarget(builder.Services)));
        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("TRP003", diagnostic.Code);
        Assert.Equal([new CompositionOwner(OwnerKind.Role, "a", typeof(A)), ServiceLedgerApplication], diagnostic.Owners);
        Assert.Equal("a", Assert.Single(error.Manifest!.Services, s => s.ServiceType == typeof(Foo)).RemovedBy!.Name);
    }
    [Fact]
    public void NestedContributionsAndValidationKeepTheOuterContributorsAttribution()
    {
        HostApplicationBuilder builder = null!;
        A.Declare = r => r.Contribute<ServicesTarget>(t =>
        {
            t.Services.AddSingleton<Foo>();
            builder.ApplyContributions(new OtherTarget(t.Services));
            t.Services.AddSingleton<Bar>();
        });
        B.Declare = r => r.Contribute<OtherTarget>(t => t.Services.AddKeyedSingleton<Client>("k"));
        builder = Builder(); builder.AddTroupe<Definition>();
        builder.ApplyContributions(new ServicesTarget(builder.Services));
        var manifest = builder.FinalizeForTest();
        var a = new CompositionOwner(OwnerKind.Role, "a", typeof(A));
        Assert.Equal(a, Assert.Single(manifest.Services, s => s.ServiceType == typeof(Foo)).Owner);
        Assert.Equal(a, Assert.Single(manifest.Services, s => s.ServiceType == typeof(Bar)).Owner);
        var keyed = Assert.Single(manifest.Services, s => s.ServiceType == typeof(Client));
        Assert.Equal("k", keyed.Key); Assert.Equal(new CompositionOwner(OwnerKind.Role, "b", typeof(B)), keyed.Owner);
    }
    [Fact]
    public void AForeignRemovalBeforeANestedContributionFailsWithTrp003()
    {
        HostApplicationBuilder builder = null!;
        A.Declare = r => r.Contribute<ServicesTarget>(t =>
        {
            t.Services.RemoveAll<Foo>();
            builder.ApplyContributions(new OtherTarget(t.Services));
        });
        B.Declare = r => r.Contribute<OtherTarget>(_ => { });
        builder = Builder(); builder.AddTroupe<Definition>();
        builder.Services.AddSingleton<Foo>();
        var error = Assert.Throws<CompositionException>(() => builder.ApplyContributions(new ServicesTarget(builder.Services)));
        Assert.Contains(error.Diagnostics, d => d.Code == "TRP003" && d.Owners[0].Name == "a");
        Assert.Equal("a", Assert.Single(error.Manifest!.Services, s => s.ServiceType == typeof(Foo)).RemovedBy!.Name);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASharedDescriptorRemovalMatchesTheActingOwnersOccurrence(bool applicationFirst)
    {
        var shared = ServiceDescriptor.Singleton<Foo, Foo>();
        A.Declare = r =>
        {
            r.Contribute<ServicesTarget>(t => t.Services.Add(shared));
            r.Contribute<OtherTarget>(t => t.Services.Remove(shared));
        };
        var builder = Builder("a"); builder.AddTroupe<Definition>();
        if (applicationFirst) builder.Services.Add(shared);
        builder.ApplyContributions(new ServicesTarget(builder.Services));
        if (!applicationFirst) builder.Services.Add(shared);
        builder.ApplyContributions(new OtherTarget(builder.Services));
        using var host = builder.Build();
        var manifest = host.Services.GetRequiredService<IComposition>().Manifest;
        var occurrences = manifest.Services.Where(s => s.ServiceType == typeof(Foo)).ToArray();
        Assert.Equal(2, occurrences.Length);
        Assert.True(Assert.Single(occurrences, o => o.Owner.Kind == OwnerKind.Application).Active);
        Assert.Equal("a", Assert.Single(occurrences, o => o.Owner.Name == "a").RemovedBy!.Name);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASharedDescriptorRemovalByAnotherContributorFailsInEitherOrder(bool applicationFirst)
    {
        var shared = ServiceDescriptor.Singleton<Foo, Foo>();
        A.Declare = r => r.Contribute<ServicesTarget>(t => t.Services.Add(shared));
        B.Declare = r => r.Contribute<OtherTarget>(t => t.Services.Remove(shared));
        var builder = Builder(); builder.AddTroupe<Definition>();
        if (applicationFirst) builder.Services.Add(shared);
        builder.ApplyContributions(new ServicesTarget(builder.Services));
        if (!applicationFirst) builder.Services.Add(shared);
        var error = Assert.Throws<CompositionException>(() => builder.ApplyContributions(new OtherTarget(builder.Services)));
        Assert.Contains(error.Diagnostics, d => d.Code == "TRP003" && d.Owners[0].Name == "b");
    }
    [Fact]
    public void AContributionThatThrowsAfterAForeignRemovalReportsTrp003WithTrp017()
    {
        A.Declare = r => r.Contribute<ServicesTarget>(t => { t.Services.RemoveAll<Foo>(); throw new InvalidOperationException("adapter failed"); });
        var builder = Builder("a"); builder.AddTroupe<Definition>();
        builder.Services.AddSingleton<Foo>();
        var error = Assert.Throws<CompositionException>(() => builder.ApplyContributions(new ServicesTarget(builder.Services)));
        Assert.Equal(["TRP017", "TRP003"], error.Diagnostics.Select(d => d.Code));
        Assert.Equal("adapter failed", error.InnerException!.Message);
        Assert.Contains(error.Manifest!.Diagnostics, d => d.Code == "TRP003");
    }
    public sealed class OtherTarget(IServiceCollection services) { public IServiceCollection Services { get; } = services; }
    private static readonly CompositionOwner ServiceLedgerApplication = new(OwnerKind.Application, "application");
    public sealed class ServicesTarget(IServiceCollection services) { public IServiceCollection Services { get; } = services; }
    [Fact]
    public void AdapterRegistrationsAreApplicationOwnedAndSnapshotsDoNotChange()
    {
        var builder = Builder("a"); var before = builder.AddTroupe<Definition>();
        builder.Services.AddSingleton<Foo>(); using var host = builder.Build();
        var after = host.Services.GetRequiredService<IComposition>().Manifest;
        Assert.DoesNotContain(before.Services, s => s.ServiceType == typeof(Foo));
        Assert.Equal(OwnerKind.Application, Assert.Single(after.Services, s => s.ServiceType == typeof(Foo)).Owner.Kind);
    }
    [Fact]
    public void CollectionCannotBeRetained()
    {
        IServiceCollection? retained = null;
        A.Declare = r => r.Services(s => retained = s);
        Builder("a").AddTroupe<Definition>();
        Assert.Throws<InvalidOperationException>(() => retained!.AddSingleton<Foo>());
    }
    [Fact]
    public void ClearOfOwnedOccurrencesIsRecordedAndForeignClearDoesNotPartiallyRemoveThem()
    {
        var empty = Builder("a"); empty.Services.Clear();
        A.Declare = r => r.Services(s => { s.AddSingleton<Foo>(); s.AddSingleton<Bar>(); s.Clear(); });
        var cleared = empty.AddTroupe<Definition>();
        Assert.Equal(2, cleared.ServiceMutations.Count(m => m.Kind == ServiceMutationKind.Clear && m.Accepted));
        Assert.DoesNotContain(cleared.Services, s => s.Active && (s.ServiceType == typeof(Foo) || s.ServiceType == typeof(Bar)));
        var normal = Builder("a");
        var added = ServiceDescriptor.Singleton<Foo, Foo>();
        A.Declare = r => r.Services(s => { s.Add(added); s.Clear(); });
        Assert.Throws<CompositionException>(() => normal.AddTroupe<Definition>());
        Assert.Contains(added, normal.Services);
    }
    [Fact]
    public void DualRoleAndComponentIdentityIsRejected()
    {
        A.Declare = r => r.Requires<Dual>();
        Assert.Contains("conflicting", Assert.Throws<CompositionException>(() => Builder("a,d").AddTroupe<DualDefinition>()).Message);
    }
    [Fact]
    public void ApplicationRulesCanPromoteAdvisories()
    {
        A.Declare = r => r.Services(s => s.AddSingleton<Foo>());
        B.Declare = r => r.Services(s => s.AddSingleton<Foo>());
        Assert.Contains("TRP001", Assert.Throws<CompositionException>(() => Builder().AddTroupe<StrictDefinition>()).Message);
    }
    [Fact]
    public void RuntimeManifestIsTheValidatedSnapshotAndDoesNotReexecuteApplicationRules()
    {
        CountingRule.Calls = 0;
        var builder = Builder("a"); builder.AddTroupe<CountingDefinition>();
        using var host = builder.Build(); var calls = CountingRule.Calls;
        var composition = host.Services.GetRequiredService<IComposition>(); var finalized = composition.Manifest;
        Assert.Same(finalized, composition.Manifest);
        Assert.Equal(calls, CountingRule.Calls);
    }
    [Fact]
    public void DefinitionWithoutRolesComposesItsRequirementsWithoutASelection()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var manifest = builder.AddTroupe<ComponentsDefinition>();
        var definition = new CompositionOwner(OwnerKind.Definition, typeof(ComponentsDefinition).FullName!, typeof(ComponentsDefinition));
        Assert.Null(manifest.Profile); Assert.Empty(manifest.Roles);
        Assert.Equal([typeof(Store).FullName!, typeof(Branch).FullName!, definition.Name], manifest.Contributors.Select(c => c.Owner.Name));
        Assert.Equal([new CompositionOwner(OwnerKind.Component, typeof(Branch).FullName!, typeof(Branch))],
            manifest.Contributors.Single(c => c.Owner == definition).Dependencies);
        Assert.Equal([OwnerKind.Definition, OwnerKind.Component, OwnerKind.Component], manifest.PathTo<Store>().Select(o => o.Kind));
        using var host = builder.Build();
        var composition = host.Services.GetRequiredService<IComposition>();
        Assert.True(composition.Includes<Store>()); Assert.Null(composition.Profile); Assert.Empty(composition.Roles);
    }
    [Theory]
    [InlineData("Profile", "TRP013")]
    [InlineData("Roles", "TRP012")]
    public void DefinitionWithoutRolesRejectsASelection(string key, string code)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration[key] = "a";
        Assert.Contains(code, Assert.Throws<CompositionException>(() => builder.AddTroupe<ComponentsDefinition>()).Message);
        var empty = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        Assert.Contains("TRP012", Assert.Throws<CompositionException>(() => empty.AddTroupe<EmptyDefinition>()).Message);
    }
    [Fact]
    public void DefinitionRequirementsAreComposedBesideEverySelection()
    {
        B.Declare = r => r.Requires<Store>();
        foreach (var (profile, roles) in new[] { ("a", new[] { "a" }), ("b", new[] { "b" }) })
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Configuration["Profile"] = profile;
            var manifest = builder.AddTroupe<SharedRequirementsDefinition>();
            Assert.Equal(roles, manifest.Roles.Select(r => r.Name));
            Assert.True(manifest.Composes<Branch>()); Assert.True(manifest.Composes<Store>());
            Assert.Single(manifest.Contributors, c => c.Owner.Type == typeof(Store));
            Assert.Equal(OwnerKind.Definition, manifest.PathTo<Branch>()[0].Kind);
        }
    }
    public sealed class ComponentsDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Requires<Branch>(); }
    public sealed class EmptyDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) { } }
    public sealed class SharedRequirementsDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Requires<Branch>().Profile("a", p => p.Role<A>()).Profile("b", p => p.Role<B>()); }
    public sealed class CountingDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<A>().Rule(new CountingRule()); }
    private sealed class CountingRule : ICompositionRule
    {
        internal static int Calls;
        public IEnumerable<CompositionDiagnostic> Evaluate(CompositionManifest manifest) { Calls++; return []; }
    }
    public sealed class StrictDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<A>().Role<B>().Rule(new PromoteDuplicates()); }
    private sealed class PromoteDuplicates : ICompositionRule
    {
        public IEnumerable<CompositionDiagnostic> Evaluate(CompositionManifest manifest) =>
            manifest.Diagnostics.Where(d => d.Code == "TRP001").Select(d => d with { Severity = DiagnosticSeverity.Error });
    }
    public sealed class DualDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<A>().Role<Dual>(); }
    [Role("d")] public sealed class Dual : IRole, IComponent
    { public void Compose(IRoleBuilder r) { } public void Compose(IComponentBuilder c) { } }
    public sealed class Definition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<A>().Role<B>(); }
    public sealed class ProfileDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Profile("both", p => p.Role<A>().Role<B>()); }
    public sealed class DuplicateRoleDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<A>().Role<A>(); }
    public sealed class DuplicateNameDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<A>().Role<SameA>(); }
    public sealed class MissingNameDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<MissingName>(); }
    public sealed class EmptyProfileDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Profile("empty", _ => { }); }
    public sealed class DuplicateProfileDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Profile("a", p => p.Role<A>()).Profile("a", p => p.Role<B>()); }
    public sealed class SharedRoleProfileDefinition : ICompositionDefinition
    { public void Define(ICompositionDefinitionBuilder d) => d.Role<A>().Profile("a", p => p.Role<A>()).Profile("both", p => p.Role<A>().Role<B>()); }
    [Role("a")] public sealed class A : IRole
    { internal static Action<IRoleBuilder> Declare = _ => { }; public void Compose(IRoleBuilder r) => Declare(r); }
    [Role("b")] public sealed class B : IRole
    { internal static Action<IRoleBuilder> Declare = _ => { }; public void Compose(IRoleBuilder r) => Declare(r); }
    [Role("a")] public sealed class SameA : IRole { public void Compose(IRoleBuilder r) { } }
    public sealed class MissingName : IRole { public void Compose(IRoleBuilder r) { } }
    public sealed class Store : IComponent { public void Compose(IComponentBuilder c) { } }
    public sealed class Branch : IComponent { public void Compose(IComponentBuilder c) => c.Requires<Store>(); }
    public sealed class FirstB : IComponent { public void Compose(IComponentBuilder c) => c.Services(s => s.TryAddSingleton<Foo>()); }
    public sealed class Cycle1 : IComponent { public void Compose(IComponentBuilder c) => c.Requires<Cycle2>(); }
    public sealed class Cycle2 : IComponent { public void Compose(IComponentBuilder c) => c.Requires<Cycle1>(); }
    public sealed class Foo;
    public sealed class Bar;
    public sealed class Client(HttpClient client) { public HttpClient Http { get; } = client; }
    public class Worker : IHostedService
    { public Task StartAsync(CancellationToken c) => Task.CompletedTask; public Task StopAsync(CancellationToken c) => Task.CompletedTask; }
    public sealed class OtherWorker : Worker;
}
