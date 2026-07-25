using Carter;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Notifications;
using DmarcAnalyzer.Api.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Modules;

public sealed class AlertsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // Alert history. Client viewers see only their granted clients' alerts.
        app.MapGet("/api/v1/alerts", async (
            int? days,
            DmarcAnalyzerDbContext db,
            ICurrentUserContext currentUser,
            CancellationToken ct) =>
        {
            var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 30, 1, 365));
            var query = db.AlertEvents.AsNoTracking().Where(e => e.DetectedAtUtc >= since);

            if (!currentUser.IsAgencyStaff)
            {
                var allowed = currentUser.AllowedClientIds;
                query = query.Where(e => allowed.Contains(e.ClientId));
            }

            var items = await query
                .OrderByDescending(e => e.DetectedAtUtc)
                .Take(500)
                .Select(e => new
                {
                    e.Id, e.ClientId, ClientName = e.Client!.Name,
                    e.DomainId, DomainName = e.Domain != null ? e.Domain.Name : null,
                    e.RuleType, e.Severity, e.Status, e.Title, e.Details,
                    e.DetectedAtUtc, e.NotifiedAtUtc,
                })
                .ToListAsync(ct);

            return Results.Ok(items);
        }).AllowClientViewer();

        // Run evaluation now rather than waiting for the next worker pass.
        app.MapPost("/api/v1/admin/alerts/evaluate", async (
            IAlertEvaluationService service,
            CancellationToken ct) =>
        {
            var result = await service.EvaluateAsync(ct);
            return Results.Ok(result);
        }).RequireAgencyAdmin();

        // Renders a client's digest for a period without sending it — lets an
        // operator see the content before it reaches a customer.
        app.MapGet("/api/v1/admin/digest/preview", async (
            Guid clientId,
            int? monthsAgo,
            IDigestService digest,
            CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                .AddMonths(-Math.Clamp(monthsAgo ?? 1, 1, 24));
            var summary = await digest.BuildAsync(clientId, start, start.AddMonths(1), ct);
            return summary is null
                ? Results.NotFound()
                : Results.Ok(new { summary, body = digest.Render(summary) });
        }).RequireAgencyAdmin();

        // Sends any digest that is due. Idempotent — a period already sent is skipped.
        app.MapPost("/api/v1/admin/digest/send", async (
            IDigestService digest,
            CancellationToken ct) =>
        {
            var result = await digest.SendDueAsync(ct);
            return Results.Ok(result);
        }).RequireAgencyAdmin();

        // Proves the SMTP relay works without waiting for something to go wrong.
        app.MapPost("/api/v1/admin/notifications/test", async (
            string? to,
            IEmailSender sender,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(to))
            {
                return Results.Json(new { error = "to query parameter is required" }, statusCode: 400);
            }

            if (!sender.IsConfigured)
            {
                return Results.Json(
                    new { error = "email is not configured; set Email:Host and Email:FromAddress" },
                    statusCode: 400);
            }

            var ok = await sender.SendAsync(
                [to.Trim()],
                "[DMARC] Test notification",
                "This is a test from your DMARC Analyzer install. If you received it, alert and digest delivery works.",
                ct);

            return ok
                ? Results.Ok(new { status = "sent" })
                : Results.Json(new { error = "send failed; check the API logs" }, statusCode: 502);
        }).RequireAgencyAdmin();
    }
}
