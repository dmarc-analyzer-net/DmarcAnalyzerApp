using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

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
    // These three keep saying mailbox_source after the table was renamed to report_source,
    // and that is deliberate. An action name is a value already written into audit_event rows
    // on every install; changing it does not rename anything, it splits the history in two —
    // rows before the upgrade say one thing, rows after say another, and the console's audit
    // filter matches on the literal. Nothing here refers to the table.
    public const string ReportSourceCreated = "mailbox_source.created";
    public const string ReportSourceUpdated = "mailbox_source.updated";
    public const string MailboxSyncTriggered = "mailbox_source.sync.triggered";

    public const string ApiCredentialIssued = "api_credential.issued";
    public const string ApiCredentialRevoked = "api_credential.revoked";

    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserGrantsChanged = "user.grants.changed";
    public const string UserDeleted = "user.deleted";

    public const string MtaStsPolicyCreated = "mta_sts_policy.created";
    public const string MtaStsPolicyUpdated = "mta_sts_policy.updated";
    public const string MtaStsPolicyDeleted = "mta_sts_policy.deleted";
    public const string MtaStsPolicyBulkApplied = "mta_sts_policy.bulk_applied";

    public const string AlertStatusChanged = "alert.status.changed";
    public const string RetentionPurgeRan = "retention.purge.ran";
    public const string NotificationRecipientAdded = "notification_recipient.added";
    public const string NotificationRecipientRemoved = "notification_recipient.removed";
    public const string DatabaseMigrated = "admin.database.migrated";

    /// <summary>
    /// An artifact carrying mailbox credentials and password hashes left the install.
    /// Worth a row even though it changes nothing.
    /// </summary>
    public const string ConfigExported = "admin.config.exported";

    /// <summary>
    /// Configuration was read back into this install — rows created or updated, and possibly
    /// password hashes replaced under the operator running it. Often the first row a
    /// recovered install has, and the one that explains where everything else came from.
    /// </summary>
    public const string ConfigImported = "admin.config.imported";

    /// <summary>
    /// Report mail was deleted from a mailbox. The only record that it existed, once the
    /// pass has run.
    /// <para>
    /// Frozen at <c>mailbox_source</c> for the reason given above the create/update actions:
    /// this is a value in existing audit rows, not a table reference.
    /// </para>
    /// </summary>
    public const string MailboxRetentionDeleted = "mailbox_source.retention.deleted";
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
            // Resolved here rather than at the call sites: this is the moment the
            // event happened, and most callers hold only an id. One indexed
            // lookup, and only for client-scoped events, which are user actions
            // rather than anything on the ingestion path.
            if (entry.ClientId is { } clientId && entry.ClientName is null)
            {
                entry.ClientName = await db.Clients
                    .AsNoTracking()
                    .Where(c => c.Id == clientId)
                    .Select(c => c.Name)
                    .FirstOrDefaultAsync(ct);
            }

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
