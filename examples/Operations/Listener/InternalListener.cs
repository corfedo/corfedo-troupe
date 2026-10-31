using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Corfedo.Troupe.AspNetCore;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe.Examples.Operations;

/// <summary>
/// The mutual-TLS listener for exposed operations, composed once per process that hosts an exposed owner.
/// Operation routes answer only on this listener, and this listener answers only operation routes.
/// </summary>
public sealed class InternalListener : IComponent
{
    public void Compose(IComponentBuilder component)
    {
        component.Requires<Routing>();
        component.Options<InternalListenerOptions, InternalListenerValidator>("Internal");
        component.Services(services => services.AddSingleton<CallerTrust>());
        component.KestrelEndpoint("internal", InternalTls.Configure);
        component.Use("internal", application => application.Use(OperationEndpoint.Admit));
        component.Endpoints(OperationEndpoint.Map);
    }
}

/// <summary>Maps caller names to their certificate files, relative to the content root.</summary>
public sealed class InternalListenerOptions
{
    public Dictionary<string, string> Callers { get; set; } = [];
}

internal sealed class InternalListenerValidator : IValidateOptions<InternalListenerOptions>
{
    public ValidateOptionsResult Validate(string? name, InternalListenerOptions options)
    {
        if (options.Callers.Count == 0) return ValidateOptionsResult.Fail("Internal:Callers lists no trusted callers.");
        var unknown = options.Callers.Keys.Where(k => !Enum.TryParse<Workload>(k, ignoreCase: true, out _)).ToArray();
        return unknown.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail($"Internal:Callers has unknown callers: {string.Join(", ", unknown)}.");
    }
}

/// <summary>Identifies callers by the SHA-256 fingerprint of their client certificate, never by the payload.</summary>
public sealed class CallerTrust
{
    private readonly Dictionary<string, Workload> callers = new(StringComparer.Ordinal);

    public CallerTrust(IOptions<InternalListenerOptions> options, IHostEnvironment environment)
    {
        foreach (var (name, path) in options.Value.Callers)
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(environment.ContentRootPath, path));
            callers.Add(Fingerprint(certificate), Enum.Parse<Workload>(name, ignoreCase: true));
        }
    }
    public bool TryIdentify(X509Certificate2? certificate, out Workload caller)
    {
        caller = default;
        return certificate is not null && callers.TryGetValue(Fingerprint(certificate), out caller);
    }
    private static string Fingerprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);
}

internal static class InternalTls
{
    internal static readonly object Marker = new();

    internal static void Configure(EndpointConfiguration endpoint)
    {
        var trust = endpoint.ListenOptions.ApplicationServices.GetRequiredService<CallerTrust>();
        endpoint.HttpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        // Pinned fingerprints are the trust decision; the issuing root is not required to be trusted by the OS.
        endpoint.HttpsOptions.ClientCertificateValidation = (certificate, _, _) => trust.TryIdentify(certificate, out _);
        endpoint.ListenOptions.Use(next => connection => { connection.Items[Marker] = true; return next(connection); });
    }
    internal static bool IsInternal(HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(Marker) == true;
}

internal static class OperationEndpoint
{
    private sealed class OperationRoute;
    private static readonly JsonSerializerOptions json = JsonSerializerOptions.Web;

    internal static Task Admit(HttpContext context, RequestDelegate next)
    {
        var operation = context.GetEndpoint()?.Metadata.GetMetadata<OperationRoute>() is not null;
        if (operation == InternalTls.IsInternal(context)) return next(context);
        // Rejected before the body is read: operation routes on other listeners, other routes on this one.
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
    internal static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/operations/{name}", InvokeAsync).WithMetadata(new OperationRoute());

    private static async Task<IResult> InvokeAsync(HttpContext context, string name, CallerTrust trust, OperationInvoker invoker, CancellationToken cancellationToken)
    {
        if (!trust.TryIdentify(context.Connection.ClientCertificate, out var caller)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (OperationTable.Find(name) is not { } operation) return Results.NotFound();
        var request = await JsonSerializer.DeserializeAsync(context.Request.Body, operation.Request, json, cancellationToken);
        if (request is null) return Results.BadRequest();
        try { return Results.Json(await invoker.InvokeAsync(caller, operation, request, cancellationToken), json); }
        catch (OperationDeniedException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
    }
}
