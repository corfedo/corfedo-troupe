using Corfedo.Troupe.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Corfedo.Troupe.Testing.Tests.TestHosts;

namespace Corfedo.Troupe.Testing.Tests.ClaimedControllers
{
    // With ControllerClaims, a process serves only the controllers that its composed components claim.
    public sealed class ControllerClaimsTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ProfileWithoutAnyClaimDoesNotServeOtherAreasControllers()
        {
            var result = await CompositionVerifier.VerifyProfilesAsync<ClaimsSplitDefinition>(_ => MvcWeb(), cancellationToken: Token);
            var plain = result.Profiles.Single(p => p.Profile!.Name == "plain").AspNetManifest!;
            static bool ServesOrders(EndpointEntry e) => e.Endpoint.DisplayName!.Contains("OrdersController");
            Assert.Contains(result.Profiles.Single(p => p.Profile!.Name == "orders").AspNetManifest!.Endpoints, ServesOrders);
            Assert.DoesNotContain(plain.Endpoints, ServesOrders);
        }

        [Fact]
        public async Task TwoComponentsClaimingOneNamespaceFail()
        {
            var result = await CompositionVerifier.VerifyAsync<DoubleClaimDefinition>(MvcWeb, cancellationToken: Token);
            var owners = result.Profiles.Single().AspNetManifest!.Endpoints.Select(e => e.Owner.Type).ToArray();
            Assert.False(result.Succeeded, $"Two composed components claim the Orders namespace; it verifies, owned by {string.Join(", ", owners.Select(o => o?.Name))}.");
        }

        [Fact]
        public async Task ClaimWithoutMvcFailsWithTrp107()
        {
            var result = await CompositionVerifier.VerifyAsync<ClaimWithoutMvcDefinition>(Web, cancellationToken: Token);
            Assert.Contains(result.Failures, f => f.Exception is CompositionException c && c.Diagnostics.Any(d => d.Code == "TRP107" && d.Message.Contains("require MVC")));
        }

        [Fact]
        public void ClaimWithoutDefinitionDeclarationFailsWithTrp107()
        {
            var failure = Assert.Throws<CompositionException>(() => MvcWeb().AddTroupe<UndeclaredClaimDefinition>());
            var diagnostic = Assert.Single(failure.Diagnostics, d => d.Code == "TRP107");
            Assert.Equal([typeof(OrdersComponent)], diagnostic.Owners.Select(o => o.Type));
        }

        [Fact]
        public async Task ClaimsWithoutMapControllersReportTheClaimedController()
        {
            var result = await CompositionVerifier.VerifyAsync<ClaimsWithoutMappingDefinition>(MvcWeb, cancellationToken: Token);
            var manifest = result.Profiles.Single().AspNetManifest!;
            Assert.Empty(manifest.Endpoints);
            Assert.Equal([typeof(Areas.Orders.OrdersController)], manifest.UnservedControllers);
            Assert.Contains(result.Failures, f => f.Exception.Message.Contains("TRP108") && f.Exception.Message.Contains(nameof(Areas.Orders.OrdersController)));
        }

        [Fact]
        public async Task ApplicationAddingMvcAfterAddTroupeIsFiltered()
        {
            var builder = Web();
            builder.Configuration["Profile"] = "plain";
            builder.AddTroupe<ClaimsSplitDefinition>();
            var parts = builder.Services.AddControllers().PartManager.ApplicationParts;
            parts.Clear();
            parts.Add(new TypesPart(typeof(Areas.Orders.OrdersController)));
            var result = await CompositionVerifier.VerifyAsync<ClaimsSplitDefinition>(() => builder, cancellationToken: Token);
            Assert.DoesNotContain(result.Profiles.Single().AspNetManifest!.Endpoints, e => e.Endpoint.DisplayName!.Contains("OrdersController"));
            Assert.Equal([typeof(Areas.Orders.OrdersController)], result.Profiles.Single().AspNetManifest!.UnservedControllers);
        }

        [Fact]
        public async Task ClaimedControllerWithoutActionsNeedsNoRoute()
        {
            var result = await CompositionVerifier.VerifyAsync<ActionlessDefinition>(() => MvcWeb(typeof(Areas.Orders.ActionlessController)), cancellationToken: Token);
            Assert.True(result.Succeeded, result.ToString());
            Assert.Equal([typeof(Areas.Orders.OrdersController)], result.Profiles.Single().AspNetManifest!.ServedControllers);
        }

        [Fact]
        public void NestedClaimsOfDifferentOwnersInOneCompositionFail()
        {
            var failure = Assert.Throws<CompositionException>(() => MvcWeb().AddTroupe<NestedClaimsDefinition>());
            var diagnostic = Assert.Single(failure.Diagnostics, d => d.Code == "TRP109");
            Assert.Equal(new[] { typeof(CatalogComponent), typeof(ReviewsComponent) }.ToHashSet(), diagnostic.Owners.Select(o => o.Type!).ToHashSet());
        }

        [Fact]
        public async Task NestedClaimsComposedOnlyInSeparateProfilesFailVerification()
        {
            var result = await CompositionVerifier.VerifyProfilesAsync<NestedClaimProfilesDefinition>(_ => CatalogWeb(), cancellationToken: Token);
            Assert.All(result.Profiles, p => Assert.DoesNotContain(p.Manifest!.Diagnostics, d => d.Severity == DiagnosticSeverity.Error));
            var failure = Assert.Single(result.Failures);
            Assert.Contains(Assert.IsType<CompositionException>(failure.Exception).Diagnostics, d => d.Code == "TRP109");
        }

        [Fact]
        public async Task ParentOnlyClaimServesItsChildNamespaces()
        {
            var result = await CompositionVerifier.VerifyProfilesAsync<ParentOnlyDefinition>(_ => CatalogWeb(), cancellationToken: Token);
            Assert.True(result.Succeeded, result.ToString());
            var catalog = result.Profiles.Single().AspNetManifest!;
            Assert.Equal([typeof(Areas.Catalog.CatalogController), typeof(Areas.Catalog.Reviews.ReviewsController)], catalog.ServedControllers);
            Assert.All(catalog.Endpoints.Where(e => e.Endpoint.DisplayName!.Contains("Catalog")), e => Assert.Equal(typeof(CatalogComponent), e.Owner.Type));
        }

        [Fact]
        public async Task OneOwnerMayClaimNestedNamespaces()
        {
            var result = await CompositionVerifier.VerifyAsync<SelfNestedDefinition>(CatalogWeb, cancellationToken: Token);
            Assert.True(result.Succeeded, result.ToString());
            Assert.Equal(2, result.Profiles.Single().AspNetManifest!.ControllerClaims.Count);
        }

        [Fact]
        public async Task RoleSelectedVerificationDoesNotJudgeCoverage()
        {
            var result = await CompositionVerifier.VerifyAsync<ClaimsRolesDefinition>(() => { var b = MvcWeb(typeof(Areas.Status.StatusController)); b.Configuration["Roles"] = "orders"; return b; }, cancellationToken: Token);
            Assert.True(result.Succeeded, result.ToString());
            Assert.NotEmpty(result.Profiles.Single().AspNetManifest!.UnservedControllers);
        }

        [Fact]
        public async Task FailedProfileSkipsCoverage()
        {
            var result = await CompositionVerifier.VerifyProfilesAsync<ClaimsSplitDefinition>(p =>
            {
                var b = MvcWeb(); if (p.Name == "orders") b.Configuration["Required"] = null; return b;
            }, cancellationToken: Token);
            Assert.DoesNotContain(result.Failures, f => f.Exception.Message.Contains("TRP108"));
            Assert.Single(result.Failures);
        }

        [Fact]
        public async Task NoRolesVerifierRejectsUnclaimedController()
        {
            var result = await CompositionVerifier.VerifyAsync<ClaimedAreaDefinition>(ClaimedAreaWeb, cancellationToken: Token);
            Assert.Contains(typeof(Areas.Unclaimed.OrphanController), result.Profiles.Single().AspNetManifest!.UnservedControllers);
            Assert.False(result.Succeeded, "The process leaves OrphanController unserved, but VerifyAsync reports success.");
        }

        [Fact]
        public async Task WorkerProfileDoesNotHideUnservedController()
        {
            var result = await CompositionVerifier.VerifyProfilesAsync<ClaimedAreaSplitDefinition>(p =>
                p.Name == "worker" ? Generic() : ClaimedAreaWeb(), cancellationToken: Token);
            Assert.Contains(typeof(Areas.Unclaimed.OrphanController), result.Profiles.Single(p => p.Profile!.Name == "mvc").AspNetManifest!.UnservedControllers);
            Assert.False(result.Succeeded, "A non-MVC profile clears the intersection of unclaimed controllers.");
        }

        [Fact]
        public async Task MvcOnlyProfilesRejectUnservedController()
        {
            var result = await CompositionVerifier.VerifyProfilesAsync<ClaimedAreaProfileDefinition>(_ => ClaimedAreaWeb(), cancellationToken: Token);
            Assert.False(result.Succeeded);
            Assert.Contains(result.Failures, f => f.Exception.Message.Contains("TRP108"));
        }

        private static WebApplicationBuilder CatalogWeb() => ControllersWeb(typeof(Areas.Catalog.CatalogController), typeof(Areas.Catalog.Reviews.ReviewsController));
        private static WebApplicationBuilder ClaimedAreaWeb() => ControllersWeb(typeof(Areas.Claimed.OwnedController), typeof(Areas.Unclaimed.OrphanController));
        private static WebApplicationBuilder MvcWeb() => MvcWeb([]);
        private static WebApplicationBuilder MvcWeb(params Type[] extra) => ControllersWeb([typeof(Areas.Orders.OrdersController), .. extra]);
        private static WebApplicationBuilder ControllersWeb(params Type[] controllers)
        {
            var builder = Web();
            builder.Configuration["Required"] = "1";
            var mvc = builder.Services.AddControllers();
            mvc.PartManager.ApplicationParts.Clear();
            mvc.PartManager.ApplicationParts.Add(new TypesPart(controllers));
            return builder;
        }
    }

    // Exposes only the listed controllers, so other controllers in this test assembly stay out of these compositions.
    public sealed class TypesPart(params Type[] types) : ApplicationPart, IApplicationPartTypeProvider
    {
        public override string Name => "types";
        public IEnumerable<System.Reflection.TypeInfo> Types => types.Select(System.Reflection.IntrospectionExtensions.GetTypeInfo);
    }

    // Mvc maps controllers and claims nothing; OrdersComponent claims the Orders namespace.
    public sealed class Mvc : IComponent
    {
        public void Compose(IComponentBuilder c) { c.Use("routing", a => a.UseRouting()); c.Endpoints(e => e.MapControllers()); }
    }
    public sealed class OrdersComponent : IComponent { public void Compose(IComponentBuilder c) { c.Requires<Mvc>(); c.Controllers<Areas.Orders.OrdersController>(); } }
    public sealed class SecondOrdersClaim : IComponent { public void Compose(IComponentBuilder c) { c.Requires<Mvc>(); c.Controllers<Areas.Orders.OrdersController>(); } }
    [Role("orders")] public sealed class OrdersRole : IRole
    {
        public void Compose(IRoleBuilder r) { r.Requires<OrdersComponent>(); r.Requires<MaybeFailing>(); }
    }
    [Role("plain")] public sealed class PlainRole : IRole { public void Compose(IRoleBuilder r) => r.Requires<Mvc>(); }
    public sealed class MaybeFailing : IComponent
    {
        public void Compose(IComponentBuilder c) => c.ReadsConfiguration("Required");
    }
    public sealed class ClaimsSplitDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Profile("orders", p => p.Role<OrdersRole>()).Profile("plain", p => p.Role<PlainRole>()); }
    }
    public sealed class ClaimsRolesDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Role<OrdersRole>().Role<PlainRole>(); }
    }
    public sealed class DoubleClaimDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Requires<OrdersComponent>().Requires<SecondOrdersClaim>(); }
    }
    public sealed class ClaimWithoutMvcDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Requires<OrdersComponent>(); }
    }
    public sealed class UndeclaredClaimDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.Requires<OrdersComponent>(); }
    }
    public sealed class ClaimsWithoutMappingDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) => d.ControllerClaims().Requires<ClaimOnly>();
    }
    public sealed class ClaimOnly : IComponent { public void Compose(IComponentBuilder c) => c.Controllers<Areas.Orders.OrdersController>(); }
    public sealed class ActionlessDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Requires<OrdersComponent>(); }
    }

    // CatalogComponent claims the Catalog namespace, ReviewsComponent its child namespace Catalog.Reviews.
    public sealed class CatalogComponent : IComponent { public void Compose(IComponentBuilder c) { c.Requires<Mvc>(); c.Controllers<Areas.Catalog.CatalogController>(); } }
    public sealed class ReviewsComponent : IComponent { public void Compose(IComponentBuilder c) { c.Requires<Mvc>(); c.Controllers<Areas.Catalog.Reviews.ReviewsController>(); } }
    public sealed class SelfNestedComponent : IComponent
    {
        public void Compose(IComponentBuilder c) { c.Requires<Mvc>(); c.Controllers<Areas.Catalog.CatalogController>(); c.Controllers<Areas.Catalog.Reviews.ReviewsController>(); }
    }
    [Role("catalog")] public sealed class CatalogRole : IRole { public void Compose(IRoleBuilder r) => r.Requires<CatalogComponent>(); }
    [Role("reviews")] public sealed class ReviewsRole : IRole { public void Compose(IRoleBuilder r) => r.Requires<ReviewsComponent>(); }
    public sealed class NestedClaimsDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Requires<CatalogComponent>().Requires<ReviewsComponent>(); }
    }
    public sealed class NestedClaimProfilesDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Profile("catalog", p => p.Role<CatalogRole>()).Profile("reviews", p => p.Role<ReviewsRole>()); }
    }
    public sealed class ParentOnlyDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Profile("catalog", p => p.Role<CatalogRole>()); }
    }
    public sealed class SelfNestedDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Requires<SelfNestedComponent>(); }
    }

    // ClaimedArea claims the Claimed namespace; no component claims Unclaimed.
    public sealed class ClaimedArea : IComponent
    {
        public void Compose(IComponentBuilder c)
        {
            c.Controllers<Areas.Claimed.OwnedController>();
            c.Use("routing", app => app.UseRouting());
            c.Endpoints(e => e.MapControllers());
        }
    }
    public sealed class ClaimedAreaDefinition : ICompositionDefinition { public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Requires<ClaimedArea>(); } }
    public sealed class ClaimedAreaSplitDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Profile("mvc", p => p.Role<MvcRole>()).Profile("worker", p => p.Role<WorkerRole>()); }
    }
    public sealed class ClaimedAreaProfileDefinition : ICompositionDefinition
    {
        public void Define(ICompositionDefinitionBuilder d) { d.Stages("routing"); d.ControllerClaims(); d.Profile("mvc", p => p.Role<MvcRole>()); }
    }
    [Role("mvc")] public sealed class MvcRole : IRole { public void Compose(IRoleBuilder r) => r.Requires<ClaimedArea>(); }
    [Role("worker")] public sealed class WorkerRole : IRole { public void Compose(IRoleBuilder r) { } }
}

namespace Corfedo.Troupe.Testing.Tests.ClaimedControllers.Areas.Orders
{
    [ApiController, Route("orders")] public sealed class OrdersController : ControllerBase { [HttpGet] public string Get() => "orders"; }
    public sealed class ActionlessController : ControllerBase { [NonAction] public string Helper() => "none"; }
}

namespace Corfedo.Troupe.Testing.Tests.ClaimedControllers.Areas.Catalog
{
    [ApiController, Route("catalog")] public sealed class CatalogController : ControllerBase { [HttpGet] public string Get() => "catalog"; }
}

namespace Corfedo.Troupe.Testing.Tests.ClaimedControllers.Areas.Catalog.Reviews
{
    [ApiController, Route("reviews")] public sealed class ReviewsController : ControllerBase { [HttpGet] public string Get() => "reviews"; }
}

namespace Corfedo.Troupe.Testing.Tests.ClaimedControllers.Areas.Status
{
    [ApiController, Route("status")] public sealed class StatusController : ControllerBase { [HttpGet] public string Get() => "ok"; }
}

namespace Corfedo.Troupe.Testing.Tests.ClaimedControllers.Areas.Claimed
{
    [ApiController, Route("claimed")] public sealed class OwnedController : ControllerBase { [HttpGet] public string Get() => "owned"; }
}

namespace Corfedo.Troupe.Testing.Tests.ClaimedControllers.Areas.Unclaimed
{
    [ApiController, Route("orphan")] public sealed class OrphanController : ControllerBase { [HttpGet] public string Get() => "orphan"; }
}
