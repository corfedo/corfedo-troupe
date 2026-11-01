using Corfedo.Troupe.Examples.Worker;
using Microsoft.Extensions.Hosting;

var builder = WorkerApplication.CreateBuilder(args);
using var host = builder.Build();
await host.RunAsync();
