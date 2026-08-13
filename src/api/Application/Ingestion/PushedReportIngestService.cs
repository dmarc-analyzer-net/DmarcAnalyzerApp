using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed record PushedReportOutcome(string SourceName, string Kind, string Result);

public sealed record PushedReportResult(
    string PayloadSha256,
    bool Replay,
    int Inserted,
    int Duplicate,
    int Failed,
    IReadOnlyList<PushedReportOutcome> Payloads);

public interface IPushedReportIngestService
{
    Task<ServiceResult<PushedReportResult>> IngestAsync(
        Guid reportSourceId, byte[] body, string? fileName, string? contentType,
        string? provenance, CancellationToken ct);
}

/// <summary>
/// Accepts report bytes posted over HTTP and puts them through exactly the path a mailbox
/// attachment takes: the same bounded extractor, the same parsers, the same ingestors.
/// <para>
/// That reuse is the point. An endpoint with its own copy of the decoding and the inserts
/// would drift from the worker's, and the two would disagree about deduplication in ways
/// nobody would notice until a report went missing.
/// </para>
/// </summary>
public sealed class PushedReportIngestService(
    DmarcAnalyzerDbContext db,
    IReportPayloadIngestor payloadIngestor,
    IOptions<WorkerOptions> options,
    ILogger<PushedReportIngestService> logger) : IPushedReportIngestService
{
    private readonly WorkerOptions _options = options.Value;

    /// <summary>Cap on the provenance document. It is a label, not a payload.</summary>
    private const int MaxProvenanceBytes = 4096;

    public async Task<ServiceResult<PushedReportResult>> IngestAsync(
        Guid reportSourceId, byte[] body, string? fileName, string? contentType,
        string? provenance, CancellationToken ct)
    {
        if (body.Length == 0)
        {
            return ServiceResult<PushedReportResult>.Failure("empty body", 400);
        }

        if (!TryReadProvenance(provenance, out var provenanceVersion, out var provenanceError))
        {
            return ServiceResult<PushedReportResult>.Failure(provenanceError!, 400);
        }

        var source = await db.ReportSources.SingleOrDefaultAsync(x => x.Id == reportSourceId, ct);
        if (source is null)
        {
            // The credential authenticated against a source that has since been deleted.
            return ServiceResult<PushedReportResult>.Failure("report source not found", 404);
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(body));

        var existing = await db.ReportIngestReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ReportSourceId == source.Id && x.PayloadSha256 == sha, ct);

        if (existing is not null)
        {
            // Answered before any work: a retrying caller gets a cheap, unambiguous yes.
            return ServiceResult<PushedReportResult>.Success(new PushedReportResult(
                sha, Replay: true, Inserted: 0, Duplicate: 0, Failed: 0, Payloads: []));
        }

        var attachment = BuildAttachment(body, fileName, contentType);

        ExtractedPayloadSet extracted;
        try
        {
            extracted = await ReportPayloadExtractor.ExtractAsync(attachment, PayloadLimits(), logger, ct);
        }
        catch (ReportPayloadTooLargeException ex)
        {
            // 413 rather than 400: the caller can act on "too big" by splitting the payload,
            // and the message names the limit that stopped it.
            return ServiceResult<PushedReportResult>.Failure(ex.Message, 413);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to extract pushed payload for report source {ReportSourceId}", source.Id);
            return ServiceResult<PushedReportResult>.Failure("payload could not be decoded", 400);
        }

        if (extracted.ArchiveTruncated)
        {
            // Refused whole, and before anything is stored, so the caller can split the
            // archive and post it again. The alternative — ingest the first N and answer
            // 200 — is the failure this exists to close: the response said inserted 5,
            // failed 0 for a twenty-entry archive, and nothing in it distinguished that
            // from having sent five. The other three expansion limits already answer 413;
            // this one used to be the exception, silently.
            await DisposeAllAsync(extracted.Payloads);
            return ServiceResult<PushedReportResult>.Failure(
                $"archive holds more than Worker:MaxReportArchiveEntries ({_options.MaxReportArchiveEntries}) " +
                "entries; split it or raise the limit", 413);
        }

        var payloads = extracted.Payloads;

        if (payloads.Count == 0)
        {
            return ServiceResult<PushedReportResult>.Failure(
                "payload contained no recognisable DMARC or TLS report", 400);
        }

        var outcomes = new List<PushedReportOutcome>();
        int inserted = 0, duplicate = 0, failed = 0;

        foreach (var payload in payloads)
        {
            await using (payload.Stream)
            {
                try
                {
                    var outcome = await payloadIngestor.IngestAsync(payload, source, ct);
                    Record(payload.SourceName, outcome.Format, outcome.Outcome);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    outcomes.Add(new PushedReportOutcome(payload.SourceName, "unknown", "failed"));
                    logger.LogWarning(ex,
                        "Failed to ingest pushed payload {PayloadName} for report source {ReportSourceId}",
                        payload.SourceName, source.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    outcomes.Add(new PushedReportOutcome(payload.SourceName, "unknown", "failed"));
                    logger.LogWarning(ex,
                        "Failed to ingest pushed payload {PayloadName} for report source {ReportSourceId}",
                        payload.SourceName, source.Id);
                }
            }
        }

        void Record(string name, string kind, ReportPayloadOutcome outcome)
        {
            switch (outcome)
            {
                case ReportPayloadOutcome.Inserted:
                    inserted++;
                    break;
                case ReportPayloadOutcome.ForeignDomainRefused:
                    // A refusal is a failure from the caller's side: nothing was stored and
                    // resending the same bytes will not change that, so it must not look
                    // like a duplicate, which means "already held".
                    failed++;
                    break;
                default:
                    duplicate++;
                    break;
            }

            outcomes.Add(new PushedReportOutcome(name, kind, outcome switch
            {
                ReportPayloadOutcome.Inserted => "inserted",
                ReportPayloadOutcome.ForeignDomainRefused => "refused-foreign-domain",
                _ => "duplicate",
            }));
        }

        // Nothing landed and something was refused: that is a failed request, not a
        // successful one carrying a failure count. A caller checking only the status code
        // would otherwise treat a wholly rejected upload as delivered, which is exactly
        // the mistake a retrying pipeline cannot afford to make.
        if (inserted == 0 && duplicate == 0 && failed > 0)
        {
            return ServiceResult<PushedReportResult>.Failure(
                $"no report in this payload could be stored ({failed} failed)", 400);
        }

        // Written only when something was stored. A payload that failed entirely must stay
        // retryable — recording the receipt anyway would turn a transient failure into a
        // permanent one, because the retry would be answered "already have it".
        if (inserted > 0 || duplicate > 0)
        {
            db.ReportIngestReceipts.Add(new ReportIngestReceipt
            {
                ReportSourceId = source.Id,
                PayloadSha256 = sha,
                PayloadCount = payloads.Count,
                Provenance = provenance,
                ProvenanceVersion = provenanceVersion,
            });
            await db.SaveChangesAsync(ct);
        }

        return ServiceResult<PushedReportResult>.Success(new PushedReportResult(
            sha, Replay: false, inserted, duplicate, failed, outcomes));
    }

    /// <summary>
    /// Validates the caller's provenance document: valid JSON, within the size cap, and
    /// carrying an integer <c>v</c>.
    /// <para>
    /// Refused rather than ignored when malformed. Silently dropping it would leave the
    /// caller believing the origin was recorded, and the whole point of provenance is to
    /// be there when someone asks months later — a field that is quietly absent is worse
    /// than one that was never promised.
    /// </para>
    /// </summary>
    /// <summary>
    /// Releases payloads extracted but never ingested, on a path that returns before the
    /// ingest loop — which is otherwise the only thing that disposes them.
    /// </summary>
    private static async Task DisposeAllAsync(IReadOnlyList<ExtractedReportPayload> payloads)
    {
        foreach (var payload in payloads)
        {
            await payload.Stream.DisposeAsync();
        }
    }

    private static bool TryReadProvenance(string? provenance, out int? version, out string? error)
    {
        version = null;
        error = null;

        if (string.IsNullOrWhiteSpace(provenance))
        {
            return true;
        }

        if (Encoding.UTF8.GetByteCount(provenance) > MaxProvenanceBytes)
        {
            error = $"provenance exceeds {MaxProvenanceBytes} bytes; it is a label, not a payload";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(provenance);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "provenance must be a JSON object";
                return false;
            }

            // ValueKind is checked first because TryGetInt32 throws rather than returning
            // false when the element is not a number — so {"v":"one"} would escape a guard
            // that only asked whether the parse succeeded.
            if (!document.RootElement.TryGetProperty("v", out var v)
                || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out var parsed))
            {
                error = "provenance must carry an integer \"v\" declaring its shape";
                return false;
            }

            version = parsed;
            return true;
        }
        catch (JsonException)
        {
            error = "provenance is not valid JSON";
            return false;
        }
    }

    /// <summary>
    /// Wraps the posted bytes as a MIME part so the extractor sees exactly what it sees for
    /// a mailbox attachment — same magic-byte detection, same container handling, same
    /// limits. The declared filename and content type are hints only; the bytes decide.
    /// </summary>
    private static MimePart BuildAttachment(byte[] body, string? fileName, string? contentType)
    {
        var (mediaType, subType) = SplitContentType(contentType);
        return new MimePart(mediaType, subType)
        {
            Content = new MimeContent(new MemoryStream(body, writable: false)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
            {
                FileName = string.IsNullOrWhiteSpace(fileName) ? "pushed-report" : fileName,
            },
        };
    }

    private static (string MediaType, string SubType) SplitContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return ("application", "octet-stream");
        }

        var value = contentType.Split(';')[0].Trim();
        var slash = value.IndexOf('/');
        return slash <= 0 || slash == value.Length - 1
            ? ("application", "octet-stream")
            : (value[..slash], value[(slash + 1)..]);
    }

    private ReportPayloadLimits PayloadLimits() => new(
        _options.MaxReportEntryBytes,
        _options.MaxReportAttachmentBytes,
        _options.MaxReportArchiveEntries);
}
