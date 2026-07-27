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
    Task WriteBackAsync(Guid domainId, EffectiveDmarcPolicy effective, CancellationToken ct);
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
    IDmarcPolicyResolver policyResolver,
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

            // Walks up when the domain publishes nothing of its own, so a subdomain covered
            // by its parent's sp= is stored as covered rather than as unprotected.
            var effective = await policyResolver.ResolveAsync(domain.Name, ct);

            if (effective.Status == RecordLookupStatus.LookupFailed)
            {
                failed++;
            }

            // The refresh pass always advances CheckedAtUtc, even when nothing moved:
            // "we verified this" is exactly what the timestamp is for.
            if (Differs(domain, effective, out var policy))
            {
                changed++;
            }

            Store(domain, policy, effective.Status, effective.InheritedFrom);
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

    public async Task WriteBackAsync(Guid domainId, EffectiveDmarcPolicy effective, CancellationToken ct)
    {
        var domain = await db.Domains.FirstOrDefaultAsync(x => x.Id == domainId, ct);
        if (domain is null)
        {
            return;
        }

        // Only persist a real difference, and crucially: touch nothing when there
        // isn't one. Mutating first and returning early would leave the tracked entity
        // dirty, so an unrelated SaveChanges later in the request would write it anyway.
        if (!Differs(domain, effective, out var policy))
        {
            return;
        }

        Store(domain, policy, effective.Status, effective.InheritedFrom);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "DNS policy for {Domain} corrected from a live lookup: {Status} p={Policy}{From}",
            domain.Name, effective.Status, policy ?? "(none)",
            effective.InheritedFrom is null ? "" : $" (from {effective.InheritedFrom})");
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
    private static bool Differs(
        Data.Entities.Domain domain, EffectiveDmarcPolicy effective, out string? policy)
    {
        policy = effective.Status switch
        {
            RecordLookupStatus.Found or RecordLookupStatus.Inherited => effective.Policy,
            RecordLookupStatus.Missing => null,
            _ => domain.DnsPolicy,
        };

        return domain.DnsPolicy != policy
            || domain.DnsLookupStatus != effective.Status
            || domain.DnsPolicyInheritedFrom != InheritedFrom(effective, domain);
    }

    /// <summary>
    /// Keeps the previous source on a failed lookup, for the same reason the policy is kept:
    /// a SERVFAIL must not turn "reject, from yulsn.io" into an unexplained reject.
    /// </summary>
    private static string? InheritedFrom(EffectiveDmarcPolicy effective, Data.Entities.Domain domain)
        => effective.Status switch
        {
            RecordLookupStatus.Inherited => effective.InheritedFrom,
            RecordLookupStatus.Found or RecordLookupStatus.Missing => null,
            _ => domain.DnsPolicyInheritedFrom,
        };

    private static void Store(
        Data.Entities.Domain domain, string? policy, string status, string? inheritedFrom)
    {
        domain.DnsPolicy = policy;
        domain.DnsLookupStatus = status;
        domain.DnsCheckedAtUtc = DateTime.UtcNow;
        domain.DnsPolicyInheritedFrom = status == RecordLookupStatus.LookupFailed
            ? domain.DnsPolicyInheritedFrom
            : inheritedFrom;
    }
}
