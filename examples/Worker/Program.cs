using Corfedo.Troupe;
using Corfedo.Troupe.Examples.Worker;
using Microsoft.Extensions.Hosting;

var builder = WorkerApplication.CreateBuilder(args);
builder.FinalizeComposition();
using var host = builder.Build();
await host.RunAsync();
