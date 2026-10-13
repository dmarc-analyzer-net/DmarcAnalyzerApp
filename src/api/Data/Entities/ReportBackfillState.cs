namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>Where a report backfill is in its life. The row keeps the last run's result after it ends.</summary>
public static class ReportBackfillStatuses
{
    /// <summary>No run has been asked for since the last one ended, or ever.</summary>
    public const string Idle = "idle";

    /// <summary>An admin asked for a run; the worker has not picked it up yet.</summary>
    public const string Requested = "requested";

    /// <summary>The worker is on it; <see cref="ReportBackfillState.Pass"/> says which pass.</summary>
    public const string Running = "running";

    public const string Completed = "completed";

    /// <summary>Stopped on an error, recorded in <see cref="ReportBackfillState.LastError"/>. A new request resumes where it stopped.</summary>
    public const string Failed = "failed";
}

/// <summary>The two passes of a run — see <see cref="ReportBackfillState.Pass"/>.</summary>
public static class ReportBackfillPasses
{
    /// <summary>Walking the archive and staging what each copy says, writing nothing to reports.</summary>
    public const string Collect = "collect";

    /// <summary>Applying the staged values to reports whose copies agree.</summary>
    public const string Apply = "apply";
}

/// <summary>
/// The report backfill's cursor and result, one row (PR 4 of docs/planning/rfc-9990-plan.md).
/// <para>
/// In the database rather than the worker's memory for the same reasons as
/// <see cref="BackupStreamState"/>: a restarted worker has to resume rather than start the
/// archive over, and the console has to answer "did it run, and what did it do?" after the
/// process that ran it is gone. Modelled on that row but with its own columns, because a
/// timestamp watermark cannot address an object — the resume point here is a key, which
/// means the same thing to any S3 client after any restart, unlike a continuation token.
/// </para>
/// </summary>
public sealed class ReportBackfillState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>One of <see cref="ReportBackfillStatuses"/>.</summary>
    public string Status { get; set; } = ReportBackfillStatuses.Idle;

    /// <summary>One of <see cref="ReportBackfillPasses"/> while running, or the pass a failed run stopped in. Null when idle or completed.</summary>
    public string? Pass { get; set; }

    /// <summary>
    /// The last archive object fully processed in the collect pass. A resumed collect lists
    /// with <c>StartAfter</c> set to it, so earlier pages are never re-read. Null before the
    /// first object and once the collect pass has ended.
    /// </summary>
    public string? LastObjectKey { get; set; }

    public DateTime? RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Archive objects read in the collect pass.</summary>
    public int ObjectsSeen { get; set; }

    /// <summary>DMARC payloads parsed out of those objects. One mail can hold several; TLS payloads are not counted.</summary>
    public int PayloadsParsed { get; set; }

    /// <summary>Payloads whose report row exists with a null format and the same source — staged for the apply pass.</summary>
    public int Matched { get; set; }

    /// <summary>Reports the apply pass wrote.</summary>
    public int Updated { get; set; }

    /// <summary>Payloads not applied: no stored row, a row that already has a format, or a copy archived by a different source than the row's.</summary>
    public int Skipped { get; set; }

    /// <summary>Reports whose archived copies disagree with each other, left unchanged.</summary>
    public int Ambiguous { get; set; }

    /// <summary>Objects that could not be downloaded, unzipped or parsed. Counted, not fatal.</summary>
    public int Failed { get; set; }

    /// <summary>Why the last run stopped, cleared when a later run completes.</summary>
    public string? LastError { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
