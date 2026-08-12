namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// Proof that a set of transport bytes has already been accepted from a source.
/// <para>
/// This is a different question from the report-level deduplication the ingest ledgers
/// answer. Those key on what a report <em>says</em> — organisation, report id, window — and
/// are what stop the same report arriving twice by different routes. This keys on the bytes
/// as posted, and exists for the caller that retries: a pipeline whose response was lost to
/// a timeout re-posts the identical payload and must be told "yes, I have that" rather than
/// made to reason about which of its reports were stored.
/// </para>
/// <para>
/// Both layers are needed. Report dedup alone would make a replay look like a payload full
/// of duplicates, which is indistinguishable from a genuinely stale payload and tells the
/// caller nothing about whether its retry worked.
/// </para>
/// </summary>
public sealed class ReportIngestReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ReportSourceId { get; set; }

    /// <summary>Hex SHA-256 of the raw posted bytes, before any decompression.</summary>
    public string PayloadSha256 { get; set; } = string.Empty;

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>How many report payloads the posted bytes turned out to contain.</summary>
    public int PayloadCount { get; set; }

    /// <summary>
    /// What the caller said about where this payload came from, verbatim.
    /// <para>
    /// The sending system knows things this one cannot: which tenant and mailbox a report
    /// was retrieved from, which message it arrived in, what its own identifier for the
    /// job was. Discarding that at the door makes "where did this report actually come
    /// from" unanswerable later, which is the question asked during an incident.
    /// </para>
    /// <para>
    /// Stored as sent rather than parsed into columns, because the useful fields differ per
    /// integration and inventing a schema for one caller's Graph metadata would fit no
    /// other. Null when the caller sent none.
    /// </para>
    /// </summary>
    public string? Provenance { get; set; }

    /// <summary>
    /// The version the caller declared for the shape of <see cref="Provenance"/>.
    /// <para>
    /// Required whenever provenance is present. An unversioned blob whose shape changes is
    /// unreadable history: nothing later can tell which rows mean what, and there is no
    /// safe way to migrate it.
    /// </para>
    /// </summary>
    public int? ProvenanceVersion { get; set; }

    public ReportSource? ReportSource { get; set; }
}
