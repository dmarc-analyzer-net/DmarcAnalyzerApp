using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Domains;

public interface IDomainIngestResolver
{
    /// <summary>
    /// The domain row for a report's domain string, created under the mailbox's
    /// default client when it does not exist yet. Takes a normalized (trimmed,
    /// lowercased) name and does not care which report format produced it.
    /// </summary>
    Task<ResolvedDomain> ResolveOrCreateAsync(Guid defaultClientId, string normalizedDomain, CancellationToken ct);
}

/// <summary>
/// Hoisted verbatim from MailboxSyncService so DMARC and TLS ingestion share
/// one create-or-get. Domain names are globally unique, so an existing domain
/// keeps whatever client it already has — the default client only applies at
/// creation. Uses raw ON CONFLICT SQL (InMemory tests cannot exercise it), and
/// callers run it outside their report transaction on purpose: a domain is
/// shared by every report for it, so rolling it back with one failed report
/// would be wrong.
/// </summary>
/// <summary>
/// A resolved domain and the client that owns it.
/// <para>
/// The owner is returned rather than assumed because it is not always the client that
/// asked. Domains are globally unique, so a source belonging to one client can resolve a
/// domain another client already owns — and the caller needs to know that happened before
/// it decides whether to store the report.
/// </para>
/// </summary>
public sealed record ResolvedDomain(Guid DomainId, Guid OwnerClientId);

public sealed class DomainIngestResolver(DmarcAnalyzerDbContext db) : IDomainIngestResolver
{
    public async Task<ResolvedDomain> ResolveOrCreateAsync(
        Guid defaultClientId, string normalizedDomain, CancellationToken ct)
    {
        var existing = await db.Domains
            .AsNoTracking()
            .Where(x => x.Name == normalizedDomain)
            .Select(x => new { x.Id, x.ClientId })
            .SingleOrDefaultAsync(ct);

        if (existing is not null)
        {
            return new ResolvedDomain(existing.Id, existing.ClientId);
        }

        var createdId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO domain
                (""Id"", ""ClientId"", ""Name"", ""IsActive"", ""CreatedAtUtc"", ""UpdatedAtUtc"")
            VALUES
                ({createdId}, {defaultClientId}, {normalizedDomain}, {true}, {DateTime.UtcNow}, {DateTime.UtcNow})
            ON CONFLICT (""Name"") DO NOTHING;
            ", ct);

        // Re-queried rather than assumed: a concurrent insert may have won the
        // conflict, and its id is the real one.
        var resolved = await db.Domains
            .AsNoTracking()
            .Where(x => x.Name == normalizedDomain)
            .Select(x => new { x.Id, x.ClientId })
            .SingleAsync(ct);

        return new ResolvedDomain(resolved.Id, resolved.ClientId);
    }
}
