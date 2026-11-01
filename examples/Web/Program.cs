using Corfedo.Troupe.AspNetCore;
using Corfedo.Troupe.Examples.Web;

var builder = ShopApplication.CreateBuilder(args);
await using var app = builder.Build();
app.UseComposition();
await app.RunAsync();
