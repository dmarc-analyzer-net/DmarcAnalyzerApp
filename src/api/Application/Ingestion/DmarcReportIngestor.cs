using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public enum DmarcReportIngestOutcome
{
    Inserted,
    Duplicate,

    /// <summary>
    /// The report's policy domain belongs to another client and this source is not allowed
    /// to ingest for domains it does not own. Nothing was stored.
    /// </summary>
    ForeignDomainRefused,
}

public interface IDmarcReportIngestor
{
    /// <summary>
    /// Stores one parsed DMARC aggregate report: the report row, its records, each
    /// record's DKIM and SPF auth results, and the ledger row — all in one transaction.
    /// Duplicate reports (the unique index) roll the whole thing back and report as such.
    /// </summary>
    Task<DmarcReportIngestOutcome> IngestAsync(
        DmarcReportParseResult parsed, ReportSource source, CancellationToken ct);
}

/// <summary>
/// The DMARC half of ingestion, lifted out of <see cref="MailboxSyncService"/> so the
/// persistence has a seam of its own — the same shape <see cref="ITlsReportIngestor"/>
/// already had for TLS reports.
/// <para>
/// Two reasons this is worth its own type. It is the only part of ingestion a caller
/// other than the mailbox loop would need, so an HTTP ingestion endpoint can write
/// through exactly the code the worker uses rather than growing a second copy of these
/// inserts. And it is testable: the sync service needs an IMAP connection before it will
/// do anything at all, which is why the two real bugs in this logic were both found in
/// production rather than by a test.
/// </para>
/// <para>
/// Same rules as the TLS ingestor: raw SQL for the <c>ON CONFLICT</c> dedup, because EF
/// cannot express it and the InMemory provider cannot execute it, and the domain resolved
/// outside the transaction because a domain is shared by every report for it rather than
/// owned by this one.
/// </para>
/// </summary>
public sealed class DmarcReportIngestor(
    DmarcAnalyzerDbContext db,
    IDomainIngestResolver domainResolver) : IDmarcReportIngestor
{
    public async Task<DmarcReportIngestOutcome> IngestAsync(
        DmarcReportParseResult parsed, ReportSource source, CancellationToken ct)
    {
        var policyDomain = parsed.PolicyDomain.Trim().ToLowerInvariant();
        var reportId = parsed.ReportId.Trim();
        var organizationName = parsed.OrganizationName.Trim();

        // Left outside the transaction on purpose: a domain is shared by every report for
        // it, not owned by this one, so rolling it back with a failed report would be
        // wrong. A domain with no reports yet is a state the console already handles.
        var domain = await domainResolver.ResolveOrCreateAsync(
            source.DefaultClientId, policyDomain, ct);

        // Refused before the transaction opens, so nothing is written and no ledger row
        // claims it. The domain itself stays — it was already there, owned by someone else,
        // and this source's opinion does not change that.
        if (!source.AllowForeignDomains && domain.OwnerClientId != source.DefaultClientId)
        {
            return DmarcReportIngestOutcome.ForeignDomainRefused;
        }

        // The report row and its records must commit together. When the report was
        // inserted before a transaction opened it auto-committed on its own, and if the
        // records insert then failed the report survived with no children — and because
        // deduplication keys on that row, every later sync saw a duplicate and skipped
        // it, so the records were never backfilled. One bad record left a report
        // permanently empty and silently wrong.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var reportEntityId = await TryInsertReportAsync(
            domain.DomainId, source.Id, organizationName, reportId, parsed, ct);

        if (!reportEntityId.HasValue)
        {
            // Already ingested. Disposing the transaction rolls back the no-op.
            return DmarcReportIngestOutcome.Duplicate;
        }

        await InsertRecordsAsync(reportEntityId.Value, parsed, ct);

        await TryInsertLedgerAsync(source, policyDomain, reportId, organizationName, parsed, ct);

        await transaction.CommitAsync(ct);
        return DmarcReportIngestOutcome.Inserted;
    }

    private async Task<Guid?> TryInsertReportAsync(
        Guid domainId, Guid reportSourceId, string organizationName, string reportId,
        DmarcReportParseResult parsed, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO dmarc_report
                (""Id"", ""DomainId"", ""ReportSourceId"", ""OrganizationName"", ""ReportId"", ""RangeBeginUtc"", ""RangeEndUtc"", ""RecordCount"", ""IngestedAtUtc"", ""PublishedPolicy"", ""SubdomainPolicy"", ""PublishedPct"", ""DkimAlignment"", ""SpfAlignment"")
            VALUES
                ({id}, {domainId}, {reportSourceId}, {organizationName}, {reportId}, {parsed.RangeBeginUtc}, {parsed.RangeEndUtc}, {parsed.RecordCount}, {DateTime.UtcNow}, {parsed.PublishedPolicy}, {parsed.SubdomainPolicy}, {parsed.PublishedPct}, {parsed.DkimAlignment}, {parsed.SpfAlignment})
            ON CONFLICT (""DomainId"", ""ReportId"", ""RangeBeginUtc"", ""RangeEndUtc"") DO NOTHING;
            ", ct);

        return rows > 0 ? id : null;
    }

    private async Task InsertRecordsAsync(
        Guid dmarcReportId, DmarcReportParseResult parsed, CancellationToken ct)
    {
        foreach (var record in parsed.Records)
        {
            var recordId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO dmarc_report_record
                    (""Id"", ""DmarcReportId"", ""SourceIp"", ""MessageCount"", ""Disposition"", ""DkimResult"", ""SpfResult"", ""HeaderFrom"", ""EnvelopeFrom"", ""EnvelopeTo"", ""ReportRangeBeginUtc"")
                VALUES
                    ({recordId}, {dmarcReportId}, {record.SourceIp}, {record.MessageCount}, {record.Disposition}, {record.DkimResult}, {record.SpfResult}, {record.HeaderFrom}, {record.EnvelopeFrom}, {record.EnvelopeTo}, {parsed.RangeBeginUtc});
                ", ct);

            foreach (var dkim in record.DkimAuthResults)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO dmarc_report_record_dkim_auth_result
                        (""Id"", ""DmarcReportRecordId"", ""Domain"", ""Selector"", ""Result"", ""HumanResult"")
                    VALUES
                        ({Guid.NewGuid()}, {recordId}, {dkim.Domain}, {dkim.Selector}, {dkim.Result}, {dkim.HumanResult});
                    ", ct);
            }

            foreach (var spf in record.SpfAuthResults)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO dmarc_report_record_spf_auth_result
                        (""Id"", ""DmarcReportRecordId"", ""Domain"", ""Scope"", ""Result"", ""HumanResult"")
                    VALUES
                        ({Guid.NewGuid()}, {recordId}, {spf.Domain}, {spf.Scope}, {spf.Result}, {spf.HumanResult});
                    ", ct);
            }
        }
    }

    private async Task TryInsertLedgerAsync(
        ReportSource source, string policyDomain, string reportId, string organizationName,
        DmarcReportParseResult parsed, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO dmarc_report_ingest
                (""Id"", ""ClientId"", ""ReportSourceId"", ""PolicyDomain"", ""ReportId"", ""ReportRangeBeginUtc"", ""ReportRangeEndUtc"", ""OrganizationName"", ""RecordCount"", ""IngestedAtUtc"")
            VALUES
                ({Guid.NewGuid()}, {source.DefaultClientId}, {source.Id}, {policyDomain}, {reportId}, {parsed.RangeBeginUtc}, {parsed.RangeEndUtc}, {organizationName}, {parsed.RecordCount}, {DateTime.UtcNow})
            ON CONFLICT (""ClientId"", ""PolicyDomain"", ""ReportId"", ""ReportRangeBeginUtc"", ""ReportRangeEndUtc"") DO NOTHING;
            ", ct);
    }
}
