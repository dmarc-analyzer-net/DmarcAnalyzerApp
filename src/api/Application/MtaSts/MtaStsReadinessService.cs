using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.MtaSts;

public interface IMtaStsReadinessService
{
    /// <summary>
    /// The promotion gate for a domain's hosted policy — null when no policy is
    /// hosted here, because there is nothing to promote (externally-hosted
    /// MTA-STS still gets monitored, just not gated). No tenancy check: the
    /// inspection service composing this has already authorized the domain.
    /// </summary>
    Task<MtaStsReadinessDto?> GetForDomainAsync(Guid domainId, CancellationToken ct);
}

public sealed class MtaStsReadinessService(
    DmarcAnalyzerDbContext db,
    ITlsRptQueryService tlsRpt) : IMtaStsReadinessService
{
    public async Task<MtaStsReadinessDto?> GetForDomainAsync(Guid domainId, CancellationToken ct)
    {
        var policy = await db.MtaStsPolicies
            .AsNoTracking()
            .Where(p => p.DomainId == domainId)
            .Select(p => new { p.Enabled, p.Mode, p.ModeChangedAtUtc })
            .SingleOrDefaultAsync(ct);

        if (policy is null)
        {
            return null;
        }

        var state = await db.MtaStsStates
            .AsNoTracking()
            .Where(s => s.DomainId == domainId)
            .Select(s => new
            {
                s.DnsRecordStatus,
                s.FetchStatus,
                s.PolicyValid,
                s.UnmatchedMxHostsJson,
                s.LastFetchOkAtUtc,
            })
            .SingleOrDefaultAsync(ct);

        // A hosted policy that has never once been reachable is mid-setup, not
        // broken — the same distinction the mta_sts_broken alert already makes.
        // Before that first success, every check reads as unknown rather than
        // failed, so a freshly created policy doesn't get told its brand-new
        // (and not-yet-propagated) DNS records are "failing".
        var everReachable = state?.LastFetchOkAtUtc is not null;

        var now = DateTime.UtcNow;
        var sample = await tlsRpt.GetGateSampleAsync(
            domainId, now.AddDays(-MtaStsReadinessEvaluator.GateWindowDays), ct);

        return MtaStsReadinessEvaluator.Evaluate(new MtaStsReadinessInput(
            policy.Enabled,
            policy.Mode,
            policy.ModeChangedAtUtc,
            StateChecked: state is not null,
            TxtOk: !everReachable ? null : state!.DnsRecordStatus == MtaStsRecordStatus.Found,
            FetchOk: !everReachable ? null : state!.FetchStatus is null ? null : state.FetchStatus == MtaStsFetchStatus.Ok,
            PolicyValid: !everReachable ? null : state!.PolicyValid,
            MxMatchOk: !everReachable ? null : state!.UnmatchedMxHostsJson is null ? null : state.UnmatchedMxHostsJson == "[]",
            sample.TotalSessions,
            sample.StsFailureSessions,
            sample.ReportCount,
            now));
    }
}
