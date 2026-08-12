namespace DmarcAnalyzer.Api.Workers;

public sealed class WorkerOptions
{
    public int ScheduleIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Messages fetched per batch. Not a cap on the run: a pass keeps drawing batches
    /// until the mailbox is drained or <see cref="MailboxDrainBudgetMinutes"/> runs
    /// out, checkpointing between them. It bounds how much work a single checkpoint
    /// covers, which is what a crash mid-drain costs in re-fetching.
    /// </summary>
    public int MaxMessagesPerSync { get; set; } = 10;

    /// <summary>
    /// How long one source may keep drawing batches before the pass moves on, so a
    /// large backlog cannot starve the other sources or the periodic passes sharing
    /// this loop. At least one batch always runs regardless.
    /// <para>
    /// Clamped below <see cref="SyncRunTimeoutMinutes"/>: the timeout cancels the run
    /// and records it as <c>partial</c>, whereas the budget is meant to stop it
    /// gracefully first, so it can never be the larger of the two.
    /// </para>
    /// </summary>
    public int MailboxDrainBudgetMinutes { get; set; } = 20;

    /// <summary>
    /// Extra days on top of a client's retention window before report mail is deleted from
    /// the mailbox.
    /// <para>
    /// Deliberately generous. Mailbox deletion is the one thing here that removes data the
    /// app does not own, and the margin is what stops a clock skew, a paused worker or a
    /// mid-incident retention change from destroying mail the database has not re-read yet.
    /// </para>
    /// </summary>
    public int MailboxRetentionGraceDays { get; set; } = 30;

    /// <summary>How often the mailbox retention pass runs. It is measured in months, so daily is plenty.</summary>
    public int MailboxRetentionIntervalHours { get; set; } = 24;

    /// <summary>
    /// Cap on a single decompressed report payload.
    /// <para>
    /// A DMARC RUA address is published in DNS, so the address of this decompressor is
    /// advertised to strangers by design. The default is far above any real aggregate
    /// report — it exists to stop a bomb, not to police size — and the log line names
    /// this setting when it trips, so an operator with a genuinely enormous sender knows
    /// exactly what to raise.
    /// </para>
    /// </summary>
    public long MaxReportEntryBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Cap on everything one mail attachment expands to, across all of its entries.
    /// <para>
    /// Not redundant with <see cref="MaxReportEntryBytes"/>. Every payload in an
    /// attachment is extracted before any of them is parsed, so they are all resident at
    /// once, and a thousand entries each just under the per-entry cap is the same attack
    /// with more steps.
    /// </para>
    /// </summary>
    public long MaxReportAttachmentBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>
    /// Cap on how many archive entries are examined in one attachment. Bounds the walk
    /// itself, so an archive of millions of tiny members costs bounded work even when
    /// nothing inside it is large.
    /// </summary>
    public int MaxReportArchiveEntries { get; set; } = 512;

    /// <summary>
    /// Ceiling on a single POST to the ingestion endpoint, before decompression.
    /// <para>
    /// The mailbox path never needed this: a mail server already caps message size, so the
    /// compressed input arrived bounded by somebody else's rule. An HTTP endpoint has no
    /// such upstream, so the request itself needs a limit of its own, separate from the
    /// expansion limits above.
    /// </para>
    /// </summary>
    public long MaxPushedReportRequestBytes { get; set; } = 32L * 1024 * 1024;

    public int MaxRetryAttempts { get; set; } = 3;
    public int RetryBaseDelaySeconds { get; set; } = 2;
    public int StaleRunTimeoutMinutes { get; set; } = 30;
    public int SyncRunTimeoutMinutes { get; set; } = 10;

    /// <summary>Master switch for retention purging.</summary>
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>How often the retention purge runs. Daily is plenty — retention is measured in months.</summary>
    public int RetentionIntervalHours { get; set; } = 24;

    /// <summary>Reports deleted per transaction, so a large backlog doesn't hold locks across the table.</summary>
    /// <summary>
    /// Refuse to start when another worker already holds the ingestion lock.
    /// <para>
    /// On by default. Two ingestion loops duplicate every sync pass and can send
    /// duplicate alert and digest email — see <see cref="WorkerSingleInstanceLock"/>
    /// for what exactly goes wrong. Turn it off only if you have a reason and know
    /// the consequences.
    /// </para>
    /// </summary>
    public bool EnforceSingleInstance { get; set; } = true;

    public int RetentionBatchSize { get; set; } = 500;
}
