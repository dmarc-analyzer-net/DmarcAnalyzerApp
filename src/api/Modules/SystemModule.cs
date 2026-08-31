using Carter;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Hosting;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

/// <summary>GET /api/v1/system/status — build, mode, and revision.</summary>
public sealed class SystemModule : ICarterModule
{
    /// <inheritdoc />
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // AppRuntimeInfo is bound from services rather than read from the environment
        // here: Program.cs has already resolved and validated the mode, and parsing it a
        // second time would give the answer two sources — the same argument this change
        // makes about the version. It is a parameter rather than a module constructor
        // dependency because Carter's analyzer refuses those.
        app.MapGet(
            "/api/v1/system/status",
            (AppRuntimeInfo runtime) => Results.Ok(
                SystemStatusResponse.For(runtime.Mode, AppVersion.Current, DateTime.UtcNow)))
            .AllowClientViewer();
    }
}
