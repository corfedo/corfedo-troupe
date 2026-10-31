using Microsoft.AspNetCore.Builder;

namespace Corfedo.Troupe.AspNetCore;

internal static class RoutingBoundary
{
    // ASP.NET Core 10 UseRouting sets this on the root application builder.
    // https://github.com/dotnet/aspnetcore/blob/v10.0.8/src/Http/Routing/src/Builder/EndpointRoutingApplicationBuilderExtensions.cs
    private const string EndpointRouteBuilder = "__EndpointRouteBuilder";
    internal static bool HasRootRouting(IApplicationBuilder application) => application.Properties.ContainsKey(EndpointRouteBuilder);
}
