using Corfedo.Troupe;
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

var builder = MessagingApplication.CreateBuilder(args);
builder.FinalizeComposition();
await using var application = builder.Build();
application.UseComposition();
await application.RunAsync();
return 0;
