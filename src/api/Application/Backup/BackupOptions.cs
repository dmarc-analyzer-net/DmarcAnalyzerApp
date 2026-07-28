namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// Continuous offload of the configuration artifact to object storage (<c>Backup:*</c>).
/// <para>
/// <see cref="Bucket"/> empty means the whole feature is inert, matching the convention
/// <c>Email:Host</c> already sets — one setting to check when asking "is this on?", and
/// no separate enabled flag that can disagree with it.
/// </para>
/// </summary>
public sealed class BackupOptions
{
    /// <summary>
    /// Destination bucket. Empty disables offload entirely; the manual export endpoint
    /// keeps working regardless.
    /// </summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// How often the offload pass runs.
    /// <para>
    /// The effective floor is <c>Worker:ScheduleIntervalSeconds</c>, because every
    /// periodic pass is gated inside that loop — with the shipped hourly schedule, 30
    /// minutes here still means roughly hourly. Shorten the schedule interval too if the
    /// cadence matters.
    /// </para>
    /// </summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Custom S3 endpoint, for MinIO, Cloudflare R2, Backblaze B2 and anything else
    /// S3-compatible. Empty targets AWS itself.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>AWS region. Ignored when <see cref="Endpoint"/> is set.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Static credentials. Leave both empty to use the ambient credential chain — an
    /// instance role or IRSA is preferable to a long-lived key in configuration.
    /// </summary>
    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>Key prefix, so one bucket can hold more than one install.</summary>
    public string Prefix { get; set; } = "dmarc";

    /// <summary>
    /// Address buckets as a path segment rather than a subdomain. Required by MinIO and
    /// most S3-compatible services; harmless on AWS.
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// Also write a dated copy of each snapshot. On by default: <c>config/latest.json</c>
    /// is overwritten every pass, so without either this or bucket versioning a single
    /// bad write is the end of the only copy.
    /// </summary>
    public bool DailySnapshot { get; set; } = true;

    /// <summary>
    /// Ship the append-only history tables (audit, alerts, digests, sync runs, ingest
    /// ledger) as their own immutable objects. These are the rows no report replay can
    /// reconstruct.
    /// </summary>
    public bool IncludeHistory { get; set; } = true;

    /// <summary>
    /// Minutes of overlap re-shipped on every history pass. Deliberately not zero: a row
    /// committed just after a pass read the clock would otherwise be skipped for good.
    /// Duplicates are free because import de-duplicates on the primary key.
    /// </summary>
    public int HistoryOverlapMinutes { get; set; } = 15;

    /// <summary>
    /// Archive the raw report mail to the bucket as it is ingested, so report history
    /// survives independently of the mailbox. Off by default — it is the largest thing
    /// this feature can be asked to store.
    /// </summary>
    public bool ArchiveReportMail { get; set; } = false;
}
