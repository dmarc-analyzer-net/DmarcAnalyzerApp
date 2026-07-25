using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.Http;

namespace DmarcAnalyzer.Api.Application.Audit;

/// <summary>Event names, kept in one place so queries and dashboards can rely on them.</summary>
public static class AuditEvents
{
    public const string LoginSucceeded = "auth.login.succeeded";
    public const string LoginFailed = "auth.login.failed";
    public const string Logout = "auth.logout";
    public const string UserRegistered = "auth.user.registered";

    public const string ClientCreated = "client.created";
    public const string ClientUpdated = "client.updated";
    public const string DomainCreated = "domain.created";
    public const string DomainUpdated = "domain.updated";
    public const string MailboxSourceCreated = "mailbox_source.created";
    public const string MailboxSourceUpdated = "mailbox_source.updated";
    public const string MailboxSyncTriggered = "mailbox_source.sync.triggered";

    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserGrantsChanged = "user.grants.changed";

    public const string AlertStatusChanged = "alert.status.changed";
    public const string RetentionPurgeRan = "retention.purge.ran";
    public const string NotificationRecipientAdded = "notification_recipient.added";
    public const string NotificationRecipientRemoved = "notification_recipient.removed";
    public const string DatabaseMigrated = "admin.database.migrated";
}

public interface IAuditLog
{
    /// <summary>
    /// Records an event performed by the signed-in user (or anonymously, for a
    /// failed sign-in). Never throws — auditing must not break the operation it
    /// is describing.
    /// </summary>
    Task RecordAsync(
        string eventType,
        string summary,
        string? targetType = null,
        Guid? targetId = null,
        Guid? clientId = null,
        string? details = null,
        string? actorEmailOverride = null,
        Guid? actorUserIdOverride = null,
        CancellationToken ct = default);

    /// <summary>Records an event performed by the system itself (worker passes).</summary>
    Task RecordSystemAsync(
        string eventType,
        string summary,
        string? details = null,
        Guid? clientId = null,
        CancellationToken ct = default);
}

public sealed class AuditLog(
    DmarcAnalyzerDbContext db,
    ICurrentUserContext currentUser,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AuditLog> logger) : IAuditLog
{
    public Task RecordAsync(
        string eventType,
        string summary,
        string? targetType = null,
        Guid? targetId = null,
        Guid? clientId = null,
        string? details = null,
        string? actorEmailOverride = null,
        Guid? actorUserIdOverride = null,
        CancellationToken ct = default)
    {
        // A successful sign-in is performed by a user who isn't authenticated
        // *yet* on this request, so the caller can name them explicitly rather
        // than the trail attributing their own login to "anonymous".
        var actorId = actorUserIdOverride ?? (currentUser.IsAuthenticated ? currentUser.UserId : null);
        return WriteAsync(new AuditEvent
        {
            ActorType = actorId.HasValue ? "user" : "anonymous",
            ActorUserId = actorId,
            ActorEmail = actorEmailOverride ?? (currentUser.IsAuthenticated ? currentUser.Email : string.Empty),
            EventType = eventType,
            Summary = summary,
            TargetType = targetType,
            TargetId = targetId,
            ClientId = clientId,
            Details = details,
        }, ct);
    }

    public Task RecordSystemAsync(
        string eventType, string summary, string? details = null, Guid? clientId = null,
        CancellationToken ct = default)
        => WriteAsync(new AuditEvent
        {
            ActorType = "system",
            ActorEmail = "system",
            EventType = eventType,
            Summary = summary,
            Details = details,
            ClientId = clientId,
        }, ct);

    private async Task WriteAsync(AuditEvent entry, CancellationToken ct)
    {
        try
        {
            var request = httpContextAccessor.HttpContext?.Request;
            entry.IpAddress = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
            entry.UserAgent = Truncate(request?.Headers.UserAgent.ToString(), 512);
            entry.Summary = Truncate(entry.Summary, 500) ?? string.Empty;
            entry.Details = Truncate(entry.Details, 4000);

            db.AuditEvents.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing audit row is bad; a failed user action because auditing
            // broke is worse. Log loudly and let the caller continue.
            logger.LogError(ex, "Failed to write audit event {EventType}", entry.EventType);
        }
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}
