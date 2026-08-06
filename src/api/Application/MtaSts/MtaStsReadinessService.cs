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
            })
            .SingleOrDefaultAsync(ct);

        var now = DateTime.UtcNow;
        var sample = await tlsRpt.GetGateSampleAsync(
            domainId, now.AddDays(-MtaStsReadinessEvaluator.GateWindowDays), ct);

        return MtaStsReadinessEvaluator.Evaluate(new MtaStsReadinessInput(
            policy.Enabled,
            policy.Mode,
            policy.ModeChangedAtUtc,
            StateChecked: state is not null,
            TxtOk: state is null ? null : state.DnsRecordStatus == MtaStsRecordStatus.Found,
            FetchOk: state?.FetchStatus is null ? null : state.FetchStatus == MtaStsFetchStatus.Ok,
            PolicyValid: state?.PolicyValid,
            MxMatchOk: state?.UnmatchedMxHostsJson is null ? null : state.UnmatchedMxHostsJson == "[]",
            sample.TotalSessions,
            sample.StsFailureSessions,
            sample.ReportCount,
            now));
    }
}
