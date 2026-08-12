using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public enum TlsReportIngestOutcome
{
    Inserted,
    Duplicate,

    /// <summary>
    /// At least one policy domain in this report belongs to another client and this source
    /// is not allowed to ingest for domains it does not own. Nothing was stored.
    /// </summary>
    ForeignDomainRefused,
}

public interface ITlsReportIngestor
{
    /// <summary>
    /// Stores one parsed TLS report: the report row, one policy row per
    /// policy-domain (resolving each domain like DMARC ingestion does), the
    /// classified failure details, and the ledger row — all in one transaction,
    /// mirroring the DMARC block. Duplicate reports (the unique index) roll the
    /// whole thing back and report as such.
    /// </summary>
    Task<TlsReportIngestOutcome> IngestAsync(
        TlsRptParseResult parsed, ReportSource source, CancellationToken ct);
}

/// <summary>
/// The TLS half of what MailboxSyncService does for DMARC, in its own class so
/// the 700-line sync service doesn't absorb a second format. Same rules: raw
/// ON CONFLICT SQL for the dedupe (InMemory tests cannot exercise it — the PR's
/// manual verification against Postgres is the proof), domains resolved outside
/// the transaction, strings truncated to column widths because reporters
/// control them.
/// </summary>
public sealed class TlsReportIngestor(
    DmarcAnalyzerDbContext db,
    IDomainIngestResolver domainResolver) : ITlsReportIngestor
{
    public async Task<TlsReportIngestOutcome> IngestAsync(
        TlsRptParseResult parsed, ReportSource source, CancellationToken ct)
    {
        var organizationName = Truncate(parsed.OrganizationName.Trim(), 255)!;
        var reportId = Truncate(parsed.ReportId.Trim(), 255)!;
        var contactInfo = Truncate(parsed.ContactInfo?.Trim(), 320);

        // One resolution per distinct domain, outside the transaction — a domain
        // is shared by every report for it, not owned by this one.
        var domainIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var policyDomain in parsed.Policies.Select(p => p.PolicyDomain).Distinct(StringComparer.Ordinal))
        {
            var domain = await domainResolver.ResolveOrCreateAsync(
                source.DefaultClientId, policyDomain, ct);

            // All or nothing. A TLS report covers several policy domains at once and its
            // counts are summed across them, so storing the permitted subset would produce
            // a report whose totals describe policies it does not contain.
            if (!source.AllowForeignDomains && domain.OwnerClientId != source.DefaultClientId)
            {
                return TlsReportIngestOutcome.ForeignDomainRefused;
            }

            domainIds[policyDomain] = domain.DomainId;
        }

        var totalSuccessful = parsed.Policies.Sum(p => p.SuccessfulSessionCount);
        var totalFailed = parsed.Policies.Sum(p => p.FailureSessionCount);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var reportEntityId = await TryInsertReportAsync(
            source.Id, organizationName, reportId, contactInfo, parsed, totalSuccessful, totalFailed, ct);

        if (!reportEntityId.HasValue)
        {
            // Already ingested. Disposing the transaction rolls back the no-op.
            return TlsReportIngestOutcome.Duplicate;
        }

        foreach (var policy in parsed.Policies)
        {
            var policyEntityId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO smtp_tls_report_policy
                    (""Id"", ""SmtpTlsReportId"", ""DomainId"", ""PolicyType"", ""PolicyDomain"", ""PolicyString"", ""MxHostPatterns"", ""SuccessfulSessionCount"", ""FailureSessionCount"", ""ReportRangeBeginUtc"", ""ReportRangeEndUtc"")
                VALUES
                    ({policyEntityId}, {reportEntityId.Value}, {domainIds[policy.PolicyDomain]}, {Truncate(policy.PolicyType, 32)}, {Truncate(policy.PolicyDomain, 255)}, {Truncate(policy.PolicyString, 4000)}, {Truncate(policy.MxHostPatterns, 2000)}, {policy.SuccessfulSessionCount}, {policy.FailureSessionCount}, {parsed.RangeBeginUtc}, {parsed.RangeEndUtc});
                ", ct);

            foreach (var detail in policy.FailureDetails)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO smtp_tls_failure_detail
                        (""Id"", ""SmtpTlsReportPolicyId"", ""ResultType"", ""FailureCategory"", ""SendingMtaIp"", ""ReceivingMxHostname"", ""ReceivingMxHelo"", ""ReceivingIp"", ""FailedSessionCount"", ""AdditionalInformation"", ""FailureReasonCode"")
                    VALUES
                        ({Guid.NewGuid()}, {policyEntityId}, {Truncate(detail.ResultType, 64)}, {TlsRptFailureClassifier.Categorize(detail.ResultType)}, {Truncate(detail.SendingMtaIp, 64)}, {Truncate(detail.ReceivingMxHostname, 255)}, {Truncate(detail.ReceivingMxHelo, 255)}, {Truncate(detail.ReceivingIp, 64)}, {detail.FailedSessionCount}, {Truncate(detail.AdditionalInformation, 2000)}, {Truncate(detail.FailureReasonCode, 255)});
                    ", ct);
            }
        }

        await TryInsertLedgerAsync(
            source, organizationName, reportId, contactInfo, parsed, totalSuccessful, totalFailed, ct);

        await transaction.CommitAsync(ct);
        return TlsReportIngestOutcome.Inserted;
    }

    private async Task<Guid?> TryInsertReportAsync(
        Guid reportSourceId, string organizationName, string reportId, string? contactInfo,
        TlsRptParseResult parsed, long totalSuccessful, long totalFailed, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO smtp_tls_report
                (""Id"", ""ReportSourceId"", ""OrganizationName"", ""ReportId"", ""ContactInfo"", ""RangeBeginUtc"", ""RangeEndUtc"", ""PolicyCount"", ""TotalSuccessfulSessionCount"", ""TotalFailureSessionCount"", ""IngestedAtUtc"")
            VALUES
                ({id}, {reportSourceId}, {organizationName}, {reportId}, {contactInfo}, {parsed.RangeBeginUtc}, {parsed.RangeEndUtc}, {parsed.Policies.Count}, {totalSuccessful}, {totalFailed}, {DateTime.UtcNow})
            ON CONFLICT (""OrganizationName"", ""ReportId"", ""RangeBeginUtc"", ""RangeEndUtc"") DO NOTHING;
            ", ct);

        return rows > 0 ? id : null;
    }

    private async Task TryInsertLedgerAsync(
        ReportSource source, string organizationName, string reportId, string? contactInfo,
        TlsRptParseResult parsed, long totalSuccessful, long totalFailed, CancellationToken ct)
    {
        var policyDomains = Truncate(
            string.Join(",", parsed.Policies.Select(p => p.PolicyDomain).Distinct(StringComparer.Ordinal)), 2000);

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO tls_report_ingest
                (""Id"", ""ClientId"", ""ReportSourceId"", ""OrganizationName"", ""ReportId"", ""ReportRangeBeginUtc"", ""ReportRangeEndUtc"", ""PolicyDomains"", ""PolicyCount"", ""TotalSuccessfulSessionCount"", ""TotalFailureSessionCount"", ""ContactInfo"", ""IngestedAtUtc"")
            VALUES
                ({Guid.NewGuid()}, {source.DefaultClientId}, {source.Id}, {organizationName}, {reportId}, {parsed.RangeBeginUtc}, {parsed.RangeEndUtc}, {policyDomains ?? string.Empty}, {parsed.Policies.Count}, {totalSuccessful}, {totalFailed}, {contactInfo}, {DateTime.UtcNow})
            ON CONFLICT (""ClientId"", ""OrganizationName"", ""ReportId"", ""ReportRangeBeginUtc"", ""ReportRangeEndUtc"") DO NOTHING;
            ", ct);
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
