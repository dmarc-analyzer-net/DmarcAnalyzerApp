namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// The protocol values that mean something to code, named once. Several separate places ask
/// "is this a mailbox we poll", and a string literal in each is how they drift apart.
/// </summary>
public static class ReportSourceProtocols
{
    /// <summary>Polled over IMAP by the worker.</summary>
    public const string Imap = "imap";

    /// <summary>
    /// Polled over POP3 by the worker. Behaves like IMAP from the outside — the same drain,
    /// the same run rows, the same retention deletion — but it checkpoints on
    /// <see cref="ReportSource.LastProcessedUidl"/> rather than on a UID, because POP3 has
    /// no UID space and no UIDVALIDITY.
    /// </summary>
    public const string Pop3 = "pop3";

    /// <summary>Pushed to over the ingestion endpoint. No mailbox behind it.</summary>
    public const string Api = "api";

    /// <summary>
    /// The protocols with a mailbox behind them, in the form a query can use.
    /// <para>
    /// An array rather than a method because most of the callers are EF queries:
    /// <c>Polled.Contains(x.Protocol)</c> translates to <c>IN</c>, while a predicate method
    /// does not translate at all. Writing the disjunction out at each call site is what this
    /// class exists to prevent — the five places that ask this question have to keep the
    /// same answer.
    /// </para>
    /// </summary>
    public static readonly string[] Polled = [Imap, Pop3];

    /// <summary>The same question outside a query, where a method reads better.</summary>
    public static bool IsPolled(string protocol) => protocol is Imap or Pop3;
}

public sealed class ReportSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = ReportSourceProtocols.Imap;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool UseTls { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string PasswordEncrypted { get; set; } = string.Empty;
    public Guid DefaultClientId { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this source may ingest reports for domains another client owns.
    /// <para>
    /// Domains are globally unique and routing is by policy domain, so a report arriving
    /// here for a domain owned by a different client is stored against that client — which
    /// is exactly what lets an agency poll one shared mailbox for many clients, and why
    /// this defaults to true.
    /// </para>
    /// <para>
    /// It is worth being able to turn off for a source whose reports should only ever
    /// concern its own client — a pushed source in particular, where a leaked credential
    /// could otherwise cause reports to appear under a client the credential has no other
    /// relationship with. Reads are unaffected either way: a credential never grants sight
    /// of another client's data.
    /// </para>
    /// </summary>
    public bool AllowForeignDomains { get; set; } = true;

    /// <summary>
    /// Delete report mail from this mailbox once it is older than the retention window the
    /// app enforces on itself.
    /// <para>
    /// Off by default, because deleting a customer's mail is not a default behaviour and
    /// some operators poll a shared mailbox that other tooling also reads. Turning it on
    /// is what gives the system one retention window instead of two: without it, the same
    /// personal data the daily purge removes from the database sits in the mailbox
    /// indefinitely, which makes an erasure request impossible to satisfy — the reports
    /// come back on the next sync.
    /// </para>
    /// </summary>
    public bool DeleteAfterRetention { get; set; }

    /// <summary>
    /// Date of the oldest message still in the polled mailbox, refreshed on each sync. Null
    /// until a sync has looked.
    /// <para>
    /// This is the evidence for the claim that the mailbox is a usable archive. Compared
    /// against the oldest report in the database it answers "how far back could we
    /// actually replay?", and after a deletion pass it is how an operator confirms the cut
    /// landed where it was supposed to.
    /// </para>
    /// </summary>
    public DateTime? OldestMessageAtUtc { get; set; }

    public DateTime? LastSuccessSyncAtUtc { get; set; }

    /// <summary>IMAP checkpoint: the highest UID fully handled. Null on a POP3 source.</summary>
    public long? LastProcessedUid { get; set; }

    /// <summary>
    /// IMAP checkpoint: the UIDVALIDITY <see cref="LastProcessedUid"/> belongs to, since a
    /// UID only identifies a message within one generation. Null on a POP3 source.
    /// </summary>
    public long? LastProcessedUidValidity { get; set; }

    /// <summary>
    /// POP3 checkpoint: the UIDL of the last message fully handled. Null on an IMAP source.
    /// <para>
    /// A separate column rather than a reuse of <see cref="LastProcessedUid"/>, because the
    /// two are not the same kind of thing and pretending otherwise costs more than a column
    /// does. A UID is an ordered integer, so "everything above it" is a range the server can
    /// resolve; a UIDL is an opaque string, so the next pass has to find it in the listing
    /// and take what follows. Storing one in the other would leave every reader — the health
    /// view, the console, this comment — unable to say which it was looking at.
    /// </para>
    /// </summary>
    public string? LastProcessedUidl { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Client? DefaultClient { get; set; }
}
