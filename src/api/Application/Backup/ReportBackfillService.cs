using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// What a backfill would find, without writing anything.
/// <para>
/// The archive side is counted by listing the prefix; the report side by rows whose
/// <c>ReportFormat</c> is null. Which rows the archive actually holds a copy of cannot be
/// known without parsing every object, so the estimate of what stays unrecorded is the
/// part that is certain: reports from pushed sources, which were never archived, and
/// reports ingested before the oldest archived day, which no object can hold.
/// </para>
/// </summary>
public sealed record ReportBackfillPreviewDto(
    bool ArchiveConfigured,
    string? Archive,
    string Prefix,
    int ArchivedObjects,
    DateOnly? OldestArchivedDay,
    DateOnly? NewestArchivedDay,
    int ReportsNotRecorded,
    int ReportsFromPushedSources,
    int ReportsFromArchivableSources,
    int ReportsIngestedBeforeTheArchive,
    int EstimatedToStayUnrecorded);

/// <summary>The cursor row as the console sees it — see <see cref="ReportBackfillState"/> for each field.</summary>
public sealed record ReportBackfillStateDto(
    string Status,
    string? Pass,
    string? LastObjectKey,
    DateTime? RequestedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    int ObjectsSeen,
    int PayloadsParsed,
    int Matched,
    int Updated,
    int Skipped,
    int Ambiguous,
    int Failed,
    string? LastError);

/// <summary>The report backfill — see <see cref="ReportBackfillService"/>.</summary>
public interface IReportBackfillService
{
    /// <summary>Counts, writes nothing.</summary>
    Task<ReportBackfillPreviewDto> PreviewAsync(CancellationToken ct);

    /// <summary>The cursor row; an idle one when no run was ever asked for.</summary>
    Task<ReportBackfillStateDto> GetStateAsync(CancellationToken ct);

    /// <summary>
    /// Asks the worker to run. 409 when no archive is configured or a run is already
    /// requested or running. After a failed run the request resumes where that run stopped.
    /// </summary>
    Task<ServiceResult<ReportBackfillStateDto>> RequestRunAsync(CancellationToken ct);

    /// <summary>
    /// The worker's entry point: runs when a run is requested, or resumes one a previous
    /// worker left running. Returns whether it did anything. Its own failures go into the
    /// cursor row, not out of this call.
    /// </summary>
    Task<bool> RunIfRequestedAsync(CancellationToken ct);
}

/// <summary>
/// Fills the report-level columns PR 1 and PR 2 of docs/planning/rfc-9990-plan.md added, on
/// reports stored before those columns existed, by re-reading their mail from the report-mail
/// archive with today's parser.
/// <para>
/// Two passes, because copies of the same report can be archived under different days — one
/// per mailbox that received it — so no single object shows all of them. <b>Collect</b> walks
/// the archive and stages what each copy says, keyed by report and object; <b>apply</b> runs
/// only once the whole archive has been seen, and writes a report only when every staged copy
/// of it agrees. Copies that disagree leave the row unchanged and are counted as ambiguous.
/// </para>
/// <para>
/// It reaches only what the archive holds: mailbox and S3 sources on installs with
/// <c>Backup:ArchiveReportMail</c> on, within the archive's lifecycle. Reports pushed to
/// <c>POST /reports</c> and reports from installs without the archive stay "not recorded".
/// And it writes <em>report-level columns only</em>: records, and therefore historic
/// <c>pass</c> dispositions stored as <c>none</c>, are not rewritten — their rows have no
/// stable key to match against, and changing them would silently change past compliance
/// figures.
/// </para>
/// <para>
/// Resumable at the object: the collect pass saves the last key it finished after each
/// object, and a restarted worker lists with <c>StartAfter</c> set to it. A key rather than
/// the continuation token because the key means the same thing after any restart. The staged
/// copies are in the database, so a restart during collect loses none, and a restart during
/// apply re-runs apply alone; a report already written has a format and is skipped, which is
/// what makes the whole thing idempotent.
/// </para>
/// </summary>
public sealed class ReportBackfillService(
    DmarcAnalyzerDbContext db,
    IObjectStorage storage,
    IDmarcReportParser parser,
    IOptions<BackupOptions> backupOptions,
    IOptions<WorkerOptions> workerOptions,
    ILogger<ReportBackfillService> logger) : IReportBackfillService
{
    private readonly BackupOptions _backup = backupOptions.Value;
    private readonly WorkerOptions _worker = workerOptions.Value;

    /// <summary>Reports written per SaveChanges in the apply pass.</summary>
    public const int ApplyBatchSize = 500;

    private const int LastErrorMaxLength = 4000;

    /// <summary>Where <see cref="ReportMailArchive.Key"/> puts report mail: under the backup prefix, dated.</summary>
    private string ArchivePrefix => $"{_backup.Prefix.Trim().Trim('/')}/reports/";

    /// <inheritdoc />
    public async Task<ReportBackfillPreviewDto> PreviewAsync(CancellationToken ct)
    {
        var notRecorded = db.DmarcReports.AsNoTracking().Where(x => x.ReportFormat == null);
        var total = await notRecorded.CountAsync(ct);
        var pushed = await notRecorded.CountAsync(
            x => x.ReportSource!.Protocol == ReportSourceProtocols.Api, ct);
        var archivable = total - pushed;

        if (!storage.IsConfigured)
        {
            return new ReportBackfillPreviewDto(
                false, null, ArchivePrefix, 0, null, null,
                total, pushed, archivable, archivable, total);
        }

        var objects = 0;
        string? firstKey = null;
        string? lastKey = null;
        string? token = null;
        do
        {
            var page = await storage.ListAsync(ArchivePrefix, null, token, ct);
            foreach (var obj in page.Objects)
            {
                objects++;
                firstKey ??= obj.Key;
                lastKey = obj.Key;
            }

            token = page.NextContinuationToken;
        }
        while (token is not null);

        var oldestDay = firstKey is null ? null : ArchivedDayOf(firstKey);
        var newestDay = lastKey is null ? null : ArchivedDayOf(lastKey);

        // A report ingested before the oldest archived day was received before it, so no
        // object can hold it. The converse is not certain — the archive may have been
        // enabled later than that day — which is why this is an estimate.
        var beforeArchive = oldestDay is null
            ? archivable
            : await notRecorded.CountAsync(
                x => x.ReportSource!.Protocol != ReportSourceProtocols.Api
                    && x.IngestedAtUtc < oldestDay.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                ct);

        return new ReportBackfillPreviewDto(
            true, storage.Describe(), ArchivePrefix, objects, oldestDay, newestDay,
            total, pushed, archivable, beforeArchive, pushed + beforeArchive);
    }

    /// <inheritdoc />
    public async Task<ReportBackfillStateDto> GetStateAsync(CancellationToken ct)
    {
        var state = await db.ReportBackfillStates.AsNoTracking().SingleOrDefaultAsync(ct);
        return ToDto(state ?? new ReportBackfillState());
    }

    /// <inheritdoc />
    public async Task<ServiceResult<ReportBackfillStateDto>> RequestRunAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured)
        {
            return ServiceResult<ReportBackfillStateDto>.Failure(
                "Backup:Bucket is not configured, so there is no report-mail archive to read.", 409);
        }

        var state = await GetOrCreateStateAsync(ct);

        if (state.Status is ReportBackfillStatuses.Requested or ReportBackfillStatuses.Running)
        {
            return ServiceResult<ReportBackfillStateDto>.Failure(
                $"A report backfill is already {state.Status}.", 409);
        }

        // A failed run resumes: its pass, cursor, counts and staged copies are kept. Anything
        // else starts over from the top of the archive.
        if (state.Status != ReportBackfillStatuses.Failed)
        {
            state.Pass = null;
            state.LastObjectKey = null;
            state.StartedAtUtc = null;
            state.ObjectsSeen = 0;
            state.PayloadsParsed = 0;
            state.Matched = 0;
            state.Updated = 0;
            state.Skipped = 0;
            state.Ambiguous = 0;
            state.Failed = 0;
        }

        state.Status = ReportBackfillStatuses.Requested;
        state.RequestedAtUtc = DateTime.UtcNow;
        state.FinishedAtUtc = null;
        state.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ServiceResult<ReportBackfillStateDto>.Success(ToDto(state));
    }

    /// <inheritdoc />
    public async Task<bool> RunIfRequestedAsync(CancellationToken ct)
    {
        var state = await db.ReportBackfillStates.SingleOrDefaultAsync(ct);
        if (state is null
            || state.Status is not (ReportBackfillStatuses.Requested or ReportBackfillStatuses.Running))
        {
            return false;
        }

        if (!storage.IsConfigured)
        {
            // Configured when requested, not any more: say so rather than spin.
            await FailAsync(state, "Backup:Bucket is no longer configured; the archive cannot be read.", ct);
            return true;
        }

        // Running on entry means the worker that was running it is gone (there is one
        // worker per database), so this is a resume, not a second run.
        state.Status = ReportBackfillStatuses.Running;
        state.StartedAtUtc ??= DateTime.UtcNow;
        state.Pass ??= ReportBackfillPasses.Collect;
        await SaveStateAsync(state, ct);

        logger.LogInformation(
            "Report backfill {Verb} in the {Pass} pass{Resume}",
            state.ObjectsSeen > 0 ? "resuming" : "starting", state.Pass,
            state.LastObjectKey is null ? string.Empty : $" after {state.LastObjectKey}");

        try
        {
            if (state.Pass == ReportBackfillPasses.Collect)
            {
                await CollectAsync(state, ct);

                state.Pass = ReportBackfillPasses.Apply;
                state.LastObjectKey = null;
                await SaveStateAsync(state, ct);
            }

            await ApplyAsync(state, ct);

            state.Status = ReportBackfillStatuses.Completed;
            state.Pass = null;
            state.FinishedAtUtc = DateTime.UtcNow;
            state.LastError = null;
            await SaveStateAsync(state, ct);

            logger.LogInformation(
                "Report backfill completed: {Objects} objects, {Payloads} payloads, {Matched} matched, " +
                "{Updated} updated, {Skipped} skipped, {Ambiguous} ambiguous, {Failed} failed",
                state.ObjectsSeen, state.PayloadsParsed, state.Matched, state.Updated,
                state.Skipped, state.Ambiguous, state.Failed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The cursor is saved per object, and the status stays running, so the next
            // worker resumes rather than starts over.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Report backfill failed in the {Pass} pass", state.Pass);
            await FailAsync(state, ex.Message, ct);
        }

        return true;
    }

    // --- Collect ---

    private async Task CollectAsync(ReportBackfillState state, CancellationToken ct)
    {
        var startAfter = state.LastObjectKey;
        string? token = null;

        do
        {
            // StartAfter only on the first page of the walk; the token carries on from there.
            var page = await storage.ListAsync(ArchivePrefix, token is null ? startAfter : null, token, ct);

            foreach (var obj in page.Objects)
            {
                ct.ThrowIfCancellationRequested();

                await CollectObjectAsync(state, obj, ct);

                // Saved per object, so a restart resumes after the last one finished.
                state.ObjectsSeen++;
                state.LastObjectKey = obj.Key;
                await SaveStateAsync(state, ct);
            }

            token = page.NextContinuationToken;
        }
        while (token is not null);
    }

    /// <summary>
    /// One archived mail: download, gunzip, parse as MIME, and run every attachment through
    /// the same extractor and parser ingestion uses. Nothing here is fatal to the run — a
    /// missing, corrupt or unparseable object is counted and the walk moves on.
    /// </summary>
    private async Task CollectObjectAsync(ReportBackfillState state, StoredObject obj, CancellationToken ct)
    {
        var sourceId = SourceIdOf(obj.Key);
        if (sourceId is null)
        {
            // Not an archived report mail — a key outside the documented layout.
            state.Failed++;
            logger.LogWarning("Report backfill: {Key} is not under the archive's layout; skipped", obj.Key);
            return;
        }

        MimeMessage message;
        try
        {
            var bytes = await storage.GetAsync(obj.Key, ct);
            if (bytes is null)
            {
                state.Failed++;
                logger.LogWarning("Report backfill: {Key} was listed but could not be fetched", obj.Key);
                return;
            }

            await using var compressed = new MemoryStream(bytes, writable: false);
            await using var gunzip = new GZipStream(compressed, CompressionMode.Decompress);
            message = await MimeMessage.LoadAsync(gunzip, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Failed++;
            logger.LogWarning(ex, "Report backfill: could not read {Key}", obj.Key);
            return;
        }

        foreach (var attachment in message.Attachments)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<ExtractedReportPayload> payloads;
            try
            {
                payloads = (await ReportPayloadExtractor.ExtractAsync(attachment, PayloadLimits(), logger, ct)).Payloads;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state.Failed++;
                logger.LogWarning(ex, "Report backfill: could not extract an attachment of {Key}", obj.Key);
                continue;
            }

            foreach (var payload in payloads)
            {
                await using (payload.Stream)
                {
                    // TLS-RPT has no report-level columns to fill.
                    if (payload.Kind == ReportPayloadKind.SmtpTlsReportJson)
                    {
                        continue;
                    }

                    DmarcReportParseResult parsed;
                    try
                    {
                        parsed = parser.Parse(payload.Stream);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        state.Failed++;
                        logger.LogWarning(ex, "Report backfill: could not parse {Payload} in {Key}", payload.SourceName, obj.Key);
                        continue;
                    }

                    state.PayloadsParsed++;
                    await StageAsync(state, parsed, sourceId.Value, obj.Key, ct);
                }
            }
        }
    }

    /// <summary>
    /// Matches a parsed copy to its stored row by the dedup key — domain, report id, range —
    /// and stages it. The dedup key does not include the source, so the same report can have
    /// been archived by two sources while only the first one's row was stored; only a copy
    /// from the row's own source is used, the rest are skipped. So are rows that already have
    /// a format: they were stored with today's columns, or a previous run wrote them.
    /// </summary>
    private async Task StageAsync(
        ReportBackfillState state, DmarcReportParseResult parsed, Guid sourceId, string key, CancellationToken ct)
    {
        var policyDomain = parsed.PolicyDomain.Trim().ToLowerInvariant();
        var reportId = parsed.ReportId.Trim();

        // Looked up, never created: a domain the database does not know is a report it did
        // not store, and a backfill must not grow the domain list.
        var domainId = await db.Domains
            .AsNoTracking()
            .Where(x => x.Name == policyDomain)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);

        var report = domainId is null
            ? null
            : await db.DmarcReports
                .AsNoTracking()
                .Where(x => x.DomainId == domainId
                    && x.ReportId == reportId
                    && x.RangeBeginUtc == parsed.RangeBeginUtc
                    && x.RangeEndUtc == parsed.RangeEndUtc)
                .Select(x => new { x.Id, x.ReportFormat, x.ReportSourceId })
                .SingleOrDefaultAsync(ct);

        if (report is null || report.ReportFormat is not null || report.ReportSourceId != sourceId)
        {
            state.Skipped++;
            return;
        }

        // The same object seen again — a resumed walk re-reading its last page — stages
        // nothing twice, and is not counted twice either. The local check covers a mail
        // that carries the same report twice: both copies are in this object, and the
        // first is not saved yet when the second is looked at.
        var seen = db.ReportBackfillCandidates.Local.Any(c => c.DmarcReportId == report.Id && c.ObjectKey == key)
            || await db.ReportBackfillCandidates.AnyAsync(c => c.DmarcReportId == report.Id && c.ObjectKey == key, ct);
        if (seen)
        {
            return;
        }

        db.ReportBackfillCandidates.Add(CandidateOf(parsed, report.Id, key));
        state.Matched++;
    }

    private static ReportBackfillCandidate CandidateOf(DmarcReportParseResult parsed, Guid reportId, string key)
    {
        var reportErrors = parsed.ReportErrors.Count == 0 ? null : JsonSerializer.Serialize(parsed.ReportErrors);
        var parseMessages = DmarcReportParseMessagesJson.Serialize(parsed.ValidationMessages);
        var warningCount = parsed.ValidationMessages.Count(x => DmarcReportParseSeverity.IsAtLeastWarning(x.Severity));

        var candidate = new ReportBackfillCandidate
        {
            DmarcReportId = reportId,
            ObjectKey = key,
            ReportFormat = parsed.ReportFormat,
            NonexistentSubdomainPolicy = parsed.NonexistentSubdomainPolicy,
            Testing = parsed.Testing,
            DiscoveryMethod = parsed.DiscoveryMethod,
            Generator = parsed.Generator,
            ReportErrors = reportErrors,
            PublishedPct = parsed.PublishedPct,
            ParseMessages = parseMessages,
            ParseWarningCount = warningCount,
        };

        // Over every value the apply pass would write, so two copies hash alike exactly when
        // applying either would leave the row the same.
        var canonical = JsonSerializer.Serialize(new
        {
            candidate.ReportFormat,
            candidate.NonexistentSubdomainPolicy,
            candidate.Testing,
            candidate.DiscoveryMethod,
            candidate.Generator,
            candidate.ReportErrors,
            candidate.PublishedPct,
            candidate.ParseMessages,
            candidate.ParseWarningCount,
        });
        candidate.ValuesHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        return candidate;
    }

    // --- Apply ---

    private async Task ApplyAsync(ReportBackfillState state, CancellationToken ct)
    {
        var groups = await db.ReportBackfillCandidates
            .AsNoTracking()
            .GroupBy(c => c.DmarcReportId)
            .Select(g => new { ReportId = g.Key, DistinctValues = g.Select(c => c.ValuesHash).Distinct().Count() })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var inBatch = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            if (group.DistinctValues > 1)
            {
                state.Ambiguous++;
                continue;
            }

            // A resumed apply pass finds the rows it already wrote with a format, and skips
            // them: that is the idempotence, not a special case.
            var report = await db.DmarcReports.SingleOrDefaultAsync(x => x.Id == group.ReportId, ct);
            if (report is null || report.ReportFormat is not null)
            {
                state.Skipped++;
                continue;
            }

            var copy = await db.ReportBackfillCandidates
                .AsNoTracking()
                .Where(c => c.DmarcReportId == group.ReportId)
                .OrderBy(c => c.ObjectKey)
                .FirstAsync(ct);

            report.ReportFormat = copy.ReportFormat;
            report.NonexistentSubdomainPolicy = copy.NonexistentSubdomainPolicy;
            report.Testing = copy.Testing;
            report.DiscoveryMethod = copy.DiscoveryMethod;
            report.Generator = copy.Generator;
            report.ReportErrors = copy.ReportErrors is null ? null : JsonSerializer.Deserialize<List<string>>(copy.ReportErrors);
            report.PublishedPct = copy.PublishedPct;
            report.ParseMessages = copy.ParseMessages;
            report.ParseWarningCount = copy.ParseWarningCount;
            report.ParseMessagesOrigin = DmarcReportParseOrigins.Backfill;
            report.ParseMessagesRecordedAtUtc = now;
            state.Updated++;

            if (++inBatch >= ApplyBatchSize)
            {
                await SaveStateAsync(state, ct);
                db.ChangeTracker.Clear();
                db.Attach(state);
                inBatch = 0;
            }
        }

        await SaveStateAsync(state, ct);

        // The staging rows have done their job. Cleared in one statement where the provider
        // can, which is every real database; the InMemory provider used by tests cannot.
        if (db.Database.IsRelational())
        {
            await db.ReportBackfillCandidates.ExecuteDeleteAsync(ct);
        }
        else
        {
            db.ReportBackfillCandidates.RemoveRange(await db.ReportBackfillCandidates.ToListAsync(ct));
            await db.SaveChangesAsync(ct);
        }
    }

    // --- Helpers ---

    private async Task FailAsync(ReportBackfillState state, string error, CancellationToken ct)
    {
        state.Status = ReportBackfillStatuses.Failed;
        state.LastError = error.Length <= LastErrorMaxLength ? error : error[..LastErrorMaxLength];
        state.FinishedAtUtc = DateTime.UtcNow;
        await SaveStateAsync(state, ct);
    }

    private async Task SaveStateAsync(ReportBackfillState state, CancellationToken ct)
    {
        state.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<ReportBackfillState> GetOrCreateStateAsync(CancellationToken ct)
    {
        var existing = await db.ReportBackfillStates.SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ReportBackfillState();
        db.ReportBackfillStates.Add(created);
        return created;
    }

    private ReportPayloadLimits PayloadLimits() => new(
        _worker.MaxReportEntryBytes,
        _worker.MaxReportAttachmentBytes,
        _worker.MaxReportArchiveEntries);

    /// <summary>
    /// The report source an archived mail belongs to, from its key:
    /// <c>{prefix}/reports/yyyy/MM/dd/{reportSourceId}/{generation}-{uid}.eml.gz</c>.
    /// Null for a key that does not follow the layout.
    /// </summary>
    public static Guid? SourceIdOf(string key)
    {
        var segments = key.Split('/');
        // .../reports/yyyy/MM/dd/{sourceId}/{name}: the source id is the second-to-last segment.
        return segments.Length >= 6 && Guid.TryParse(segments[^2], out var id) ? id : null;
    }

    /// <summary>The day segment of an archive key, or null for a key that does not follow the layout.</summary>
    public static DateOnly? ArchivedDayOf(string key)
    {
        var segments = key.Split('/');
        if (segments.Length < 6)
        {
            return null;
        }

        var (y, m, d) = (segments[^5], segments[^4], segments[^3]);
        return DateOnly.TryParseExact($"{y}-{m}-{d}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }

    private static ReportBackfillStateDto ToDto(ReportBackfillState state) => new(
        state.Status,
        state.Pass,
        state.LastObjectKey,
        state.RequestedAtUtc,
        state.StartedAtUtc,
        state.FinishedAtUtc,
        state.ObjectsSeen,
        state.PayloadsParsed,
        state.Matched,
        state.Updated,
        state.Skipped,
        state.Ambiguous,
        state.Failed,
        state.LastError);
}
