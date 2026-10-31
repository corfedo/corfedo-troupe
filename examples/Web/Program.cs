using Corfedo.Troupe;
using Corfedo.Troupe.AspNetCore;
using Corfedo.Troupe.Examples.Web;

var builder = WebExample.CreateBuilder(args);
builder.FinalizeComposition();
await using var app = builder.Build();
app.UseComposition();
await app.RunAsync();
