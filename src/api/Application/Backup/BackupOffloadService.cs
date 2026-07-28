using System.Text;
using System.Text.Json;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Backup;

public interface IBackupOffloadService
{
    /// <summary>
    /// Ships the configuration snapshot and, when enabled, whatever history rows are new.
    /// Safe to run repeatedly: the snapshot is idempotent by overwrite and the history
    /// streams re-ship a deliberate overlap that import de-duplicates.
    /// </summary>
    Task<BackupOffloadResult> RunAsync(CancellationToken ct);

    Task<BackupStatusDto> GetStatusAsync(CancellationToken ct);
}

/// <summary>
/// The half-hourly offload.
/// <para>
/// Two shapes of object, for two shapes of data. Configuration is a <em>snapshot</em>:
/// small, mutable, overwritten at <c>config/latest.json</c> so a recovery has one obvious
/// thing to fetch. History is <em>append-only</em>: written once into dated objects that
/// are never rewritten, which is what makes shipping it every thirty minutes cheap.
/// </para>
/// </summary>
public sealed class BackupOffloadService(
    DmarcAnalyzerDbContext db,
    IBackupExportService exportService,
    IObjectStorage storage,
    IConfiguration configuration,
    IOptions<BackupOptions> options,
    ILogger<BackupOffloadService> logger) : IBackupOffloadService
{
    /// <summary>The snapshot stream's name in <c>backup_stream_state</c>.</summary>
    public const string ConfigStream = "config";

    private readonly BackupOptions _options = options.Value;

    public async Task<BackupOffloadResult> RunAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured)
        {
            return new BackupOffloadResult(false, null, new Dictionary<string, int>(), null);
        }

        // Refused, not warned. Without a key the app stores mailbox passwords in plaintext,
        // and shipping those to a bucket is materially worse than leaving them in Postgres:
        // the blast radius stops being "the database" and becomes "wherever this bucket is
        // readable from".
        if (string.IsNullOrWhiteSpace(configuration[CredentialProtectionExtensions.KeyConfigPath]))
        {
            const string reason =
                "Security:CredentialEncryptionKey is not configured, so mailbox passwords are stored " +
                "in plaintext and will not be shipped to object storage. Configure a key to enable offload.";

            logger.LogError("Backup offload refused: {Reason}", reason);
            await RecordAttemptAsync(ConfigStream, null, reason, ct);

            return new BackupOffloadResult(false, null, new Dictionary<string, int>(), reason);
        }

        var prefix = _options.Prefix.Trim().Trim('/');
        var historyObjects = new Dictionary<string, int>();
        string? snapshotKey = null;

        try
        {
            snapshotKey = await OffloadSnapshotAsync(prefix, ct);
            await RecordAttemptAsync(ConfigStream, DateTime.UtcNow, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Backup snapshot offload to {Destination} failed", storage.Describe());
            await RecordAttemptAsync(ConfigStream, null, Truncate(ex.Message), ct);

            return new BackupOffloadResult(true, null, historyObjects, ex.Message);
        }

        if (_options.IncludeHistory)
        {
            // Each stream carries its own watermark and its own failure, so one broken
            // stream neither blocks the others nor makes the snapshot look failed.
            foreach (var stream in BackupHistoryStreams.All)
            {
                try
                {
                    var written = await OffloadHistoryStreamAsync(prefix, stream, ct);
                    historyObjects[stream.Name] = written;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Backup history offload failed for stream {Stream}", stream.Name);
                    await RecordAttemptAsync(stream.Name, null, Truncate(ex.Message), ct);
                }
            }
        }

        return new BackupOffloadResult(true, snapshotKey, historyObjects, null);
    }

    /// <summary>
    /// Validate, stage, verify, then promote.
    /// <para>
    /// <c>config/latest.json</c> is the one key this feature overwrites, which makes it the
    /// one place a successful-looking pass can destroy the only good copy. So the document
    /// is parsed back before it is sent, written to a staging key, confirmed to have
    /// arrived at its full length, and only then copied over <c>latest.json</c>.
    /// </para>
    /// </summary>
    private async Task<string> OffloadSnapshotAsync(string prefix, CancellationToken ct)
    {
        var result = await exportService.ExportAsync(allowPlaintextCredentials: false, ct);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Error);
        }

        var artifact = result.Value!;
        var json = BackupJson.Serialize(artifact);
        var bytes = Encoding.UTF8.GetBytes(json);

        Validate(json);

        var stagingKey = $"{prefix}/config/.staging/latest.json";
        var latestKey = $"{prefix}/config/latest.json";

        await storage.PutAsync(stagingKey, bytes, "application/json", ct);

        var stagedLength = await storage.GetLengthAsync(stagingKey, ct);
        if (stagedLength != bytes.Length)
        {
            throw new InvalidOperationException(
                $"staged snapshot is {stagedLength?.ToString() ?? "missing"} bytes, expected {bytes.Length}; " +
                $"leaving {latestKey} untouched");
        }

        if (_options.DailySnapshot)
        {
            // Written before the promotion, so a dated copy exists even if the copy to
            // latest.json is what fails.
            await storage.CopyAsync(
                stagingKey,
                $"{prefix}/config/{artifact.Manifest.ExportedAtUtc:yyyy-MM-dd}.json",
                ct);
        }

        await storage.CopyAsync(stagingKey, latestKey, ct);

        logger.LogInformation(
            "Backup snapshot offloaded to {Destination} ({Bytes} bytes, {Clients} client(s), " +
            "{Sources} mailbox source(s))",
            storage.Describe(), bytes.Length, artifact.Clients.Count, artifact.MailboxSources.Count);

        return latestKey;
    }

    /// <summary>
    /// A document that parses, has a manifest, and describes at least one client. Not a
    /// deep check — it is there to catch a truncated or empty serialization before it
    /// replaces a good artifact, which is the failure that would otherwise be discovered
    /// during a recovery.
    /// </summary>
    private static void Validate(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("manifest", out var manifest)
            || !manifest.TryGetProperty("formatVersion", out _))
        {
            throw new InvalidOperationException("serialized snapshot has no manifest; refusing to ship it");
        }

        if (!document.RootElement.TryGetProperty("clients", out var clients)
            || clients.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                "serialized snapshot contains no clients; refusing to overwrite latest.json with it");
        }
    }

    /// <summary>
    /// Ships rows newer than the watermark minus an overlap window, into an object named
    /// for the moment it was written.
    /// <para>
    /// The overlap is the whole trick. A bare "newer than the last timestamp" cursor loses
    /// any row that commits just after a pass read the clock — invisibly, and for good.
    /// Re-sending a few minutes of rows costs nothing because import inserts history by
    /// primary key and skips what it already has.
    /// </para>
    /// </summary>
    private async Task<int> OffloadHistoryStreamAsync(
        string prefix,
        BackupHistoryStream stream,
        CancellationToken ct)
    {
        var state = await GetOrCreateStateAsync(stream.Name, ct);
        var overlap = TimeSpan.FromMinutes(Math.Max(0, _options.HistoryOverlapMinutes));
        var since = state.WatermarkUtc?.Subtract(overlap);

        var rows = await stream.ReadAsync(db, since, ct);
        if (rows.Count == 0)
        {
            await RecordAttemptAsync(stream.Name, DateTime.UtcNow, null, ct);
            return 0;
        }

        // JSON Lines: one row per line, so an object can be appended to conceptually and
        // read back a row at a time without holding the whole stream in memory.
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(JsonSerializer.Serialize(row, CompactOptions));
        }

        var now = DateTime.UtcNow;
        var key = $"{prefix}/history/{stream.Name}/{now:yyyy}/{now:MM}/{now:yyyy-MM-ddTHHmm}.jsonl";

        await storage.PutAsync(key, Encoding.UTF8.GetBytes(builder.ToString()), "application/x-ndjson", ct);

        var newest = rows.Max(stream.TimestampOf);
        await RecordAttemptAsync(stream.Name, now, null, ct, watermark: newest);

        logger.LogInformation(
            "Backup history stream {Stream} shipped {Rows} row(s) to {Key}", stream.Name, rows.Count, key);

        return 1;
    }

    /// <summary>
    /// History is machine-read in bulk, so it is written compactly — unlike the snapshot,
    /// which a person reads.
    /// </summary>
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public async Task<BackupStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var states = await db.BackupStreamStates.AsNoTracking().OrderBy(x => x.Stream).ToListAsync(ct);
        var config = states.FirstOrDefault(x => x.Stream == ConfigStream);

        var versioning = storage.IsConfigured
            ? await storage.GetVersioningAsync(ct)
            : ObjectStorageVersioning.Unknown;

        return new BackupStatusDto(
            OffloadConfigured: storage.IsConfigured,
            Destination: storage.IsConfigured ? storage.Describe() : null,
            CredentialsProtected: !string.IsNullOrWhiteSpace(
                configuration[CredentialProtectionExtensions.KeyConfigPath]),
            BucketVersioning: versioning.ToString().ToLowerInvariant(),
            IntervalMinutes: _options.IntervalMinutes,
            HistoryEnabled: _options.IncludeHistory,
            ReportArchiveEnabled: _options.ArchiveReportMail,
            LastSuccessfulOffloadAtUtc: config?.LastSuccessAtUtc,
            LastAttemptAtUtc: config?.LastAttemptAtUtc,
            LastError: config?.LastError,
            Streams: [.. states
                .Where(x => x.Stream != ConfigStream)
                .Select(x => new BackupStreamStatusDto(
                    x.Stream, x.WatermarkUtc, x.LastSuccessAtUtc, x.LastError))]);
    }

    private async Task<BackupStreamState> GetOrCreateStateAsync(string stream, CancellationToken ct)
    {
        var existing = await db.BackupStreamStates.SingleOrDefaultAsync(x => x.Stream == stream, ct);
        if (existing is not null)
        {
            return existing;
        }

        var created = new BackupStreamState { Stream = stream };
        db.BackupStreamStates.Add(created);
        await db.SaveChangesAsync(ct);

        return created;
    }

    /// <summary>
    /// Records the outcome of one stream's attempt. A success clears the stored error, so
    /// a lingering <c>LastError</c> always means "still failing" rather than "failed once,
    /// months ago".
    /// </summary>
    private async Task RecordAttemptAsync(
        string stream,
        DateTime? successAtUtc,
        string? error,
        CancellationToken ct,
        DateTime? watermark = null)
    {
        var state = await GetOrCreateStateAsync(stream, ct);

        state.LastAttemptAtUtc = DateTime.UtcNow;
        state.UpdatedAtUtc = DateTime.UtcNow;

        if (successAtUtc.HasValue)
        {
            state.LastSuccessAtUtc = successAtUtc;
            state.LastError = null;

            // Only ever moves forward. An overlap read must not drag it backwards.
            if (watermark.HasValue && (!state.WatermarkUtc.HasValue || watermark > state.WatermarkUtc))
            {
                state.WatermarkUtc = watermark;
            }
        }
        else
        {
            state.LastError = error;
        }

        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value)
        => value.Length <= 4000 ? value : value[..4000];
}
