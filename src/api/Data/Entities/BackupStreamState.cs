namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// Where the backup offload got to, per stream.
/// <para>
/// It has to be in the database rather than in the worker's memory for two reasons. The
/// periodic passes gate on in-memory fields, so a restarted or crash-looping worker
/// re-runs everything — for an append-only history stream that would mean re-shipping
/// from the beginning of time. And the console has to be able to answer "when did this
/// last succeed?" for an operator who is deciding whether to trust the backup, which is
/// not a question a process-local field can answer after a restart.
/// </para>
/// </summary>
public sealed class BackupStreamState
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Which stream this row tracks: <c>config</c> for the snapshot, or the history table
    /// name for an append-only stream.
    /// </summary>
    public string Stream { get; set; } = string.Empty;

    /// <summary>
    /// Highest row timestamp shipped so far. Null for <c>config</c>, which is a snapshot
    /// and has nothing to advance through.
    /// <para>
    /// Read back with an overlap window rather than used as an exact cursor: a row that
    /// commits just after a pass reads the clock would otherwise never be shipped.
    /// </para>
    /// </summary>
    public DateTime? WatermarkUtc { get; set; }

    public DateTime? LastSuccessAtUtc { get; set; }

    /// <summary>
    /// Why the last attempt failed, kept after a subsequent success is recorded only
    /// until that success clears it. An offload that has been failing quietly for a week
    /// is the failure mode this exists to make visible.
    /// </summary>
    public string? LastError { get; set; }

    public DateTime? LastAttemptAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
