using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>How a refresh pass went, for the worker log.</summary>
public sealed record DnsPolicyRefreshResult(int Checked, int Changed, int Failed);

public interface IDnsPolicyCache
{
    /// <summary>
    /// Re-resolves the DMARC record for every active domain and stores the policy.
    /// Least-recently-checked first, so an interrupted pass makes progress next time.
    /// </summary>
    Task<DnsPolicyRefreshResult> RefreshAllAsync(CancellationToken ct);

    /// <summary>
    /// Stores a record resolved elsewhere — the domain detail page already performs a
    /// live lookup, so a difference from the cached value is corrected for free.
    /// A no-op when nothing changed, to keep page views from writing on every request.
    /// </summary>
    Task WriteBackAsync(Guid domainId, DnsDmarcRecordDto record, CancellationToken ct);
}

/// <summary>
/// Caches each domain's published DMARC policy on the domain row so list views can
/// render the real policy from one query instead of one DNS lookup per row.
///
/// Deliberately writes only the three Dns* columns: UpdatedAtUtc means "an operator
/// changed this domain", and a background refresh is not that. Nothing here records an
/// audit event either — auditing is explicit per endpoint, and a cache refresh is not
/// an operator action.
/// </summary>
public sealed class DnsPolicyCache(
    DmarcAnalyzerDbContext db,
    IDnsTxtResolver dns,
    ILogger<DnsPolicyCache> logger) : IDnsPolicyCache
{
    public async Task<DnsPolicyRefreshResult> RefreshAllAsync(CancellationToken ct)
    {
        var domains = await db.Domains
            .Where(x => x.IsActive)
            .OrderBy(x => x.DnsCheckedAtUtc == null ? 0 : 1)
            .ThenBy(x => x.DnsCheckedAtUtc)
            .ToListAsync(ct);

        var changed = 0;
        var failed = 0;

        foreach (var domain in domains)
        {
            ct.ThrowIfCancellationRequested();

            var record = RecordInspectionService.ParseDmarc(
                await dns.ResolveAsync($"_dmarc.{domain.Name}", ct));

            if (record.Status == RecordLookupStatus.LookupFailed)
            {
                failed++;
            }

            // The refresh pass always advances CheckedAtUtc, even when nothing moved:
            // "we verified this" is exactly what the timestamp is for.
            if (Differs(domain, record, out var policy))
            {
                changed++;
            }

            Store(domain, policy, record.Status);
        }

        if (domains.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "DNS policy refresh: checked {Checked}, changed {Changed}, lookup failures {Failed}",
            domains.Count, changed, failed);

        return new DnsPolicyRefreshResult(domains.Count, changed, failed);
    }

    public async Task WriteBackAsync(Guid domainId, DnsDmarcRecordDto record, CancellationToken ct)
    {
        var domain = await db.Domains.FirstOrDefaultAsync(x => x.Id == domainId, ct);
        if (domain is null)
        {
            return;
        }

        // Only persist a real difference, and crucially: touch nothing when there
        // isn't one. Mutating first and returning early would leave the tracked entity
        // dirty, so an unrelated SaveChanges later in the request would write it anyway.
        if (!Differs(domain, record, out var policy))
        {
            return;
        }

        Store(domain, policy, record.Status);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "DNS policy for {Domain} corrected from a live lookup: {Status} p={Policy}",
            domain.Name, record.Status, record.Policy ?? "(none)");
    }

    /// <summary>
    /// Whether a resolved record says anything new, and what the policy should become.
    /// Pure — callers decide whether to write, so an unchanged result can leave the
    /// entity untouched.
    ///
    /// A failed lookup keeps the last known policy rather than blanking it: a transient
    /// SERVFAIL must not make a p=reject domain look unprotected. Only a successful
    /// lookup that finds no record clears it.
    /// </summary>
    private static bool Differs(Data.Entities.Domain domain, DnsDmarcRecordDto record, out string? policy)
    {
        policy = record.Status switch
        {
            RecordLookupStatus.Found => record.Policy,
            RecordLookupStatus.Missing => null,
            _ => domain.DnsPolicy,
        };

        return domain.DnsPolicy != policy || domain.DnsLookupStatus != record.Status;
    }

    private static void Store(Data.Entities.Domain domain, string? policy, string status)
    {
        domain.DnsPolicy = policy;
        domain.DnsLookupStatus = status;
        domain.DnsCheckedAtUtc = DateTime.UtcNow;
    }
}
