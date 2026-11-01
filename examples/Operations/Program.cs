using Corfedo.Troupe.AspNetCore;
using Corfedo.Troupe.Examples.Operations;

if (args is ["certificates", ..])
{
    var directory = Path.Combine(AppContext.BaseDirectory, "certificates");
    DevelopmentCertificates.Write(directory, Workload.Api.Name());
    Console.WriteLine($"Wrote development certificates to {directory}");
    return 0;
}

var builder = OperationsApplication.CreateBuilder(args);
await using var application = builder.Build();
application.UseComposition();
await application.RunAsync();
return 0;
