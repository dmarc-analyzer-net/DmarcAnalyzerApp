using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Middleware;

/// <summary>
/// Authenticates <c>Authorization: Bearer</c> machine credentials, ahead of the cookie
/// session middleware.
/// <para>
/// A second authentication path is real surface, so the two are kept as separate as
/// possible: this one only ever looks at a bearer token that parses as ours, only ever
/// populates <see cref="MachineCallerContext"/>, and never touches the user context. A
/// request is one or the other, and downstream code asks the question it means rather than
/// a shared "is authenticated" flag.
/// </para>
/// <para>
/// It authenticates and stops. Whether the endpoint accepts a machine caller at all is
/// <see cref="RoleAuthorizationMiddleware"/>'s decision, so a credential that authenticates
/// perfectly still reaches nothing it was not issued for.
/// </para>
/// </summary>
public sealed class MachineAuthMiddleware(RequestDelegate next)
{
    /// <summary>
    /// How stale <c>LastUsedAtUtc</c> may get before it is rewritten. Without this a busy
    /// pipeline turns every ingest into an extra UPDATE for a column nobody reads in real
    /// time; an hour is plenty to answer "is this credential still in use".
    /// </summary>
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromHours(1);

    public async Task InvokeAsync(
        HttpContext context, DmarcAnalyzerDbContext db, MachineCallerContext machineCaller)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var presented = header["Bearer ".Length..].Trim();
        if (!MachineToken.TryParse(presented, out var tokenId, out var secret))
        {
            // Not one of ours. Left for the session middleware to reject in its own way,
            // rather than answering here and telling a prober which scheme this app uses.
            await next(context);
            return;
        }

        var credential = await db.ApiCredentials
            .SingleOrDefaultAsync(x => x.TokenId == tokenId, context.RequestAborted);

        var now = DateTime.UtcNow;

        // Verified before the usability checks, and unconditionally: answering "revoked"
        // faster than "wrong secret" would tell a caller holding a bad token that the id
        // half was real.
        var secretMatches = credential is not null
            && MachineToken.VerifySecret(secret, credential.TokenHash);

        if (credential is null || !secretMatches || !credential.IsUsable(now) || credential.ReportSourceId is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid credential" });
            return;
        }

        machineCaller.Set(
            credential.Id, credential.Name, credential.Kind, credential.ReportSourceId.Value);

        if (credential.LastUsedAtUtc is null || now - credential.LastUsedAtUtc > LastUsedWriteInterval)
        {
            credential.LastUsedAtUtc = now;
            await db.SaveChangesAsync(context.RequestAborted);
        }

        await next(context);
    }
}
