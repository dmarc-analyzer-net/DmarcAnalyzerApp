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

    /// <summary>
    /// Polled from an S3-compatible bucket by the worker. Not a mailbox — the objects are
    /// report files, or whole messages, that something else has already delivered — but the
    /// same pass reads it, so it is polled in exactly the sense the worker means.
    /// </summary>
    public const string S3 = "s3";

    /// <summary>Pushed to over the ingestion endpoint. Nothing to poll.</summary>
    public const string Api = "api";

    /// <summary>
    /// The protocols the worker goes and fetches from, in the form a query can use.
    /// <para>
    /// An array rather than a method because most of the callers are EF queries:
    /// <c>Polled.Contains(x.Protocol)</c> translates to <c>IN</c>, while a predicate method
    /// does not translate at all. Writing the disjunction out at each call site is what this
    /// class exists to prevent — the five places that ask this question have to keep the
    /// same answer.
    /// </para>
    /// </summary>
    public static readonly string[] Polled = [Imap, Pop3, S3];

    /// <summary>The same question outside a query, where a method reads better.</summary>
    public static bool IsPolled(string protocol) => protocol is Imap or Pop3 or S3;

    /// <summary>
    /// The protocols that reach a mailbox over the network with a host, a port and a login.
    /// <para>
    /// Narrower than <see cref="Polled"/> since S3 joined it, and the difference is what the
    /// create path validates on: an S3 source has a bucket and a region instead of a host and
    /// a port, and may have no credential at all when the ambient chain supplies one.
    /// </para>
    /// </summary>
    public static bool IsMailbox(string protocol) => protocol is Imap or Pop3;
}

public sealed class ReportSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = ReportSourceProtocols.Imap;

    /// <summary>Mailbox host. Empty on an S3 or pushed source, which has no host.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Mailbox port. Zero on an S3 or pushed source.</summary>
    public int Port { get; set; }

    public bool UseTls { get; set; } = true;

    /// <summary>
    /// The login half of the credential: a mailbox username, or an S3 access key id. Empty on
    /// a pushed source, and legitimately empty on an S3 source using the ambient credential
    /// chain — an instance role or IRSA, which is preferable to a long-lived key in a row.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The secret half, AES-256-GCM at rest: a mailbox password, or an S3 secret access key.
    /// One column for both because it is the same secret to everything that handles it — the
    /// encryption, the re-protection of legacy plaintext, and the rule that it is never read
    /// back out over the API.
    /// </summary>
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

    /// <summary>Bucket name. Set only on an S3 source.</summary>
    public string? S3Bucket { get; set; }

    /// <summary>
    /// Key prefix to poll, so one bucket can serve more than one client or hold more than
    /// reports. Null or empty polls the whole bucket.
    /// <para>
    /// Worth setting for more than tidiness: a pass lists every key under the prefix, so the
    /// prefix is also what bounds the cost of each poll.
    /// </para>
    /// </summary>
    public string? S3Prefix { get; set; }

    /// <summary>AWS region. Ignored when <see cref="S3Endpoint"/> is set.</summary>
    public string? S3Region { get; set; }

    /// <summary>
    /// Custom S3 endpoint, for MinIO, Cloudflare R2, Backblaze B2 and anything else
    /// S3-compatible. Null targets AWS itself.
    /// </summary>
    public string? S3Endpoint { get; set; }

    /// <summary>
    /// Address the bucket as a path segment rather than a subdomain. Required by MinIO and
    /// most S3-compatible services; harmless on AWS. Defaults true, matching the backup
    /// client, because the compatible services are the ones that break without it.
    /// </summary>
    public bool S3ForcePathStyle { get; set; } = true;

    /// <summary>
    /// S3 checkpoint: when the last object fully handled was last modified. Null on any other
    /// protocol.
    /// <para>
    /// A timestamp rather than a key, and that is the whole design. S3 lists keys in
    /// lexicographic order and offers <c>StartAfter</c> to resume from one, which is tempting
    /// and wrong here: nothing makes an object's key sort in the order it arrived, so a
    /// provider writing keys with a random or hashed prefix would drop every new object that
    /// happened to sort below the checkpoint — silently, and for ever. Ordering by
    /// last-modified is the only ordering the bucket actually guarantees relates to arrival.
    /// </para>
    /// <para>
    /// It costs a listing of the whole prefix on every pass. That is the price of not losing
    /// reports, and <see cref="S3Prefix"/> is what bounds it.
    /// </para>
    /// </summary>
    public DateTime? LastProcessedObjectAtUtc { get; set; }

    /// <summary>
    /// S3 checkpoint, tiebreaker half: the key of the last object fully handled.
    /// <para>
    /// Needed because last-modified is not unique — a bulk upload can stamp thousands of
    /// objects on the same second. The pass orders by (last-modified, key) and resumes
    /// strictly after that pair, so objects sharing a timestamp are neither repeated nor
    /// skipped.
    /// </para>
    /// </summary>
    public string? LastProcessedObjectKey { get; set; }

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
