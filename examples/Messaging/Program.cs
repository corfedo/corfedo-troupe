using Corfedo.Troupe.AspNetCore;
using Corfedo.Troupe.Examples.Messaging;
using JasperFx;

if (args is ["codegen", ..])
{
    var generationHost = MessagingApplication.CreateGenerationBuilder(Directory.GetCurrentDirectory()).Build();
    try { return await generationHost.RunJasperFxCommands(args); }
    finally
    {
        if (generationHost is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else generationHost.Dispose();
    }
}

if (args is ["durable", "resources", "setup", .. var provisioningArgs])
{
    var host = MessagingApplication.CreateDurableProvisioningBuilder(provisioningArgs).Build();
    try { return await host.RunJasperFxCommands(["resources", "setup"]); }
    finally
    {
        if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else host.Dispose();
    }
}

var builder = args is ["durable", .. var servingArgs]
    ? MessagingApplication.CreateDurableBuilder(servingArgs)
    : MessagingApplication.CreateBuilder(args);
await using var application = builder.Build();
application.UseComposition();
await application.RunAsync();
return 0;
