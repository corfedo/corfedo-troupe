// Runs the Operations example as one combined process, or as api, gateway and ai processes (--Topology split).
var builder = DistributedApplication.CreateBuilder(args);
var topology = builder.Configuration["Topology"] ?? "combined";

// The example's own command writes the development certificates that every process reads.
var certificates = builder.AddProject<Projects.Operations>("certificates", o => o.ExcludeLaunchProfile = true).WithArgs("certificates");

IResourceBuilder<ProjectResource> Operations(string name) =>
    builder.AddTroupeProject<Projects.Operations>(name).WaitForCompletion(certificates);
// The internal listener uses mutual TLS with the example's own certificates, so it is declared as an HTTPS endpoint and
// Aspire's developer certificate is not configured; the package adds `public`.
IResourceBuilder<ProjectResource> Internal(IResourceBuilder<ProjectResource> resource) =>
    resource.WithHttpsEndpoint(name: "internal").WithoutHttpsCertificate();

switch (topology)
{
    case "combined":
        Internal(Operations("operations")).WithTroupeProfile("combined");
        break;
    case "split":
        var gateway = Internal(Operations("gateway")).WithTroupeProfile("gateway");
        var ai = Internal(Operations("ai")).WithTroupeProfile("ai");
        Operations("api").WithTroupeProfile("api")
            .WithEnvironment("Roles__Remote__gateway", gateway.GetEndpoint("internal"))
            .WithEnvironment("Roles__Remote__ai", ai.GetEndpoint("internal"))
            .WithEnvironment("Workload__Identities__api__Certificate", "certificates/api.pem")
            .WithEnvironment("Workload__Identities__api__Key", "certificates/api.key")
            .WithEnvironment("Workload__Root", "certificates/root.pem");
        break;
    default:
        throw new InvalidOperationException($"Unknown topology {topology}; use combined or split.");
}

await builder.Build().RunAsync();
