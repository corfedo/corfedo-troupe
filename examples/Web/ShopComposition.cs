using Corfedo.Troupe.AspNetCore;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe.Examples.Web;

public sealed class ShopComposition : ICompositionDefinition
{
    public void Define(ICompositionDefinitionBuilder definition)
    {
        definition.Requires<CatalogComponent>().Requires<OrdersComponent>();
        definition.Stages("routing");
    }
}

public sealed class CatalogComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<PublicIngressComponent>().Requires<ProductsComponent>();
        component.Endpoints(endpoints => endpoints.MapGet("/products", (ProductCatalog catalog) => catalog.All));
    }
}

public sealed class OrdersComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<PublicIngressComponent>().Requires<ProductsComponent>();
        component.Endpoints(endpoints => endpoints.MapPost("/orders", (OrderRequest order, ProductCatalog catalog) =>
            catalog.Find(order.Sku) is { } product
                ? Results.Ok(new OrderConfirmation(product.Sku, order.Quantity, product.Price * order.Quantity))
                : Results.NotFound()));
    }
}

// Catalog and Orders both require it; it is composed once.
public sealed class ProductsComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Options<ProductOptions>("Products");
        component.Services(services => services.AddSingleton<ProductCatalog>());
    }
}

public sealed class PublicIngressComponent : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.KestrelEndpoint("public");
        component.Use("routing", application => application.UseRouting());
    }
}

public sealed class ProductOptions
{
    public List<Product> Items { get; set; } = [];
}

public sealed class Product
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

public sealed class ProductCatalog(IOptions<ProductOptions> options)
{
    public IReadOnlyList<Product> All => options.Value.Items;
    public Product? Find(string sku) => options.Value.Items.FirstOrDefault(p => p.Sku == sku);
}

public sealed record OrderRequest(string Sku, int Quantity);
public sealed record OrderConfirmation(string Sku, int Quantity, decimal Total);
