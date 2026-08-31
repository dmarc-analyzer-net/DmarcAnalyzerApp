using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// An S3-compatible bucket as a report source, behind the same seam as the two mail
/// protocols. The drain budget, batched checkpoints, archive-before-parse, run rows and
/// retention deletion are all shared; what is here is what a bucket does differently.
/// <para>
/// The differences that matter:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Objects, not messages.</b> What is in the bucket depends on who fills it — bare report
/// files from a provider, or whole RFC822 messages from an SES delivery rule. Both are
/// handled, decided per object; see <see cref="PolledObjectContent"/>.
/// </description></item>
/// <item><description>
/// <b>The checkpoint is a timestamp, not a key.</b> S3 lists lexicographically and offers
/// <c>StartAfter</c>, which is the obvious resume and the wrong one for deciding what has
/// arrived: nothing makes a key sort in arrival order, so a provider using a hashed or random
/// prefix would have every new object that sorted below the checkpoint skipped for ever.
/// Ordering on last-modified is the only ordering a bucket gives that relates to arrival at
/// all. The cost is a listing of the prefix per pass, which the prefix itself is what bounds —
/// and where it doesn't, the per-pass key cap does, with its own cursor
/// (<see cref="ReportSource.S3ReadListingCursorKey"/>) resuming the <em>listing itself</em>
/// across passes so a prefix bigger than the cap is covered in full over several passes rather
/// than never past the cap. That cursor answers a different question than the arrival
/// checkpoint above and the same objection does not apply to it — see its own doc comment.
/// </description></item>
/// <item><description>
/// <b>Credentials are per source, and optional.</b> A bucket named in one row may live in a
/// different account from the next, so the access key lives on the row rather than in
/// configuration. Leaving it empty falls back to the SDK's ambient chain, which is what an
/// instance role or IRSA looks like and is the better answer where it is available.
/// </description></item>
/// <item><description>
/// <b>Deletion is immediate and singular.</b> No flag-then-expunge, no delete-at-QUIT: one
/// <c>DeleteObject</c> per key, effective at once. So the prune session's commit has nothing
/// to do, and there is no window in which a dropped connection undoes anything.
/// </description></item>
/// </list>
/// </summary>
public sealed class S3ReportSourceTransport(
    IOptions<WorkerOptions> options,
    ILogger<S3ReportSourceTransport> logger,
    int maxKeysPerPass = S3ReportSourceTransport.DefaultMaxKeysPerPass) : IPolledSourceTransport
{
    private readonly WorkerOptions _options = options.Value;

    /// <summary>
    /// S3 returns at most 1000 keys per request and the SDK pages on a continuation token.
    /// Bounded here as well so a bucket somebody points at by mistake — a data lake, an
    /// unprefixed backup bucket — costs a bounded listing rather than an unbounded one pass,
    /// not an unbounded one ever: <see cref="ListAsync"/>'s cursor is what carries a listing
    /// past this cap over to the next pass rather than dropping it. The pass says so in the
    /// log when it stops, because a silent cap reads as "that is all there is".
    /// <para>
    /// A constructor parameter rather than a plain constant so a test can shrink it far below
    /// a real bucket's size and exercise the multi-pass cursor without uploading 100,000
    /// objects. DI never supplies <c>int</c>, so every real caller gets the default.
    /// </para>
    /// </summary>
    public const int DefaultMaxKeysPerPass = 100_000;

    private readonly int _maxKeysPerPass = maxKeysPerPass;

    /// <inheritdoc />
    public string Protocol => ReportSourceProtocols.S3;

    /// <summary>One object in the bucket, as much of it as a pass needs.</summary>
    public sealed record S3ObjectRef(string Key, DateTime LastModifiedUtc);

    /// <inheritdoc />
    public async Task<IPolledReadSession> OpenForReadAsync(
        ReportSource source, string secret, CancellationToken ct)
    {
        var client = CreateClient(source, secret);

        try
        {
            var (listed, nextCursor) = await ListAsync(client, source, source.S3ReadListingCursorKey, ct);

            // Written back straight away rather than through ApplyGeneration: the listing has
            // already happened by the time this line runs, so there is nothing left to defer,
            // and every path that persists reportSource afterward — the batched checkpoint
            // commits, the success save, the exception path's re-attach — picks up whatever is
            // set on it here the same way it already picks up any other property.
            source.S3ReadListingCursorKey = nextCursor;

            var ordered = Order(listed);

            var pending = SelectObjectsPastCheckpoint(
                ordered, source.LastProcessedObjectAtUtc, source.LastProcessedObjectKey);

            return new S3ReadSession(
                client,
                source.S3Bucket ?? string.Empty,
                ordered,
                pending,
                _options.MaxReportAttachmentBytes);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IPolledPruneSession> OpenForPruneAsync(
        ReportSource source, string secret, DateTime cutoffUtc, bool dryRun, CancellationToken ct)
    {
        var client = CreateClient(source, secret);

        try
        {
            var (listed, nextCursor) = await ListAsync(client, source, source.S3PruneListingCursorKey, ct);
            source.S3PruneListingCursorKey = nextCursor;

            var ordered = Order(listed);

            // Last-modified, not the report's own dates: an object that never parsed has to
            // age out too, or the bucket accumulates permanent failures for ever. Same rule
            // as the mailbox pass, on the only timestamp a bucket has.
            var eligible = ordered.Where(x => x.LastModifiedUtc < cutoffUtc).ToArray();
            var survivors = ordered.Where(x => x.LastModifiedUtc >= cutoffUtc).ToArray();

            logger.LogInformation(
                "S3 retention scan for report source {ReportSourceId} listed {Listed} object(s) and " +
                "found {Eligible} last modified before {Cutoff:yyyy-MM-dd}",
                source.Id, ordered.Count, eligible.Length, cutoffUtc);

            // What each eligible object archived under, which is not always its last-modified
            // date: a message-shaped object with its own Date header archives under that Date
            // (see PolledObjectContent.Parse), and the archive-existence check below has to
            // agree with it or a real archived copy reads as unarchived for ever and is never
            // deleted. Only asked for objects already known eligible, the same restraint IMAP
            // applies by fetching envelopes only for its own eligible set.
            var archivedAtUtc = new DateTime[eligible.Length];
            for (var index = 0; index < eligible.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                archivedAtUtc[index] = await ResolveArchivedAtUtcAsync(
                    client, source.S3Bucket ?? string.Empty, eligible[index], ct);
            }

            return new S3PruneSession(
                client, source.S3Bucket ?? string.Empty, eligible, archivedAtUtc, survivors, dryRun, logger);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The one ordering everything here agrees on: oldest first, key breaking a tie. The
    /// drain, the checkpoint comparison and the retention scan all depend on it being the
    /// same ordering, so it is written once.
    /// </summary>
    public static IReadOnlyList<S3ObjectRef> Order(IEnumerable<S3ObjectRef> objects)
        => [.. objects.OrderBy(x => x.LastModifiedUtc).ThenBy(x => x.Key, StringComparer.Ordinal)];

    /// <summary>
    /// The objects a pass should actually read: those after the checkpoint, in listing order.
    /// <para>
    /// The checkpoint is a pair — last-modified and key — and the comparison is on the pair,
    /// not on the timestamp alone. A bulk upload can stamp thousands of objects on the same
    /// second, so "strictly newer than the timestamp" would skip every sibling of the
    /// checkpointed object, and "newer or equal" would re-read all of them on every pass for
    /// ever. Resuming strictly after the pair is what makes both of those impossible.
    /// </para>
    /// <para>
    /// A checkpoint whose object has since been deleted still works, unlike the POP3
    /// equivalent: the pair is a position in an ordering rather than a name that has to
    /// resolve, so a missing object leaves the resume point perfectly well defined.
    /// </para>
    /// <para>
    /// <see cref="PolledItemRef.Token"/> is the index into <paramref name="ordered"/>, which
    /// is how the session gets back to an object's timestamp when it checkpoints.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PolledItemRef> SelectObjectsPastCheckpoint(
        IReadOnlyList<S3ObjectRef> ordered, DateTime? lastProcessedAtUtc, string? lastProcessedKey)
    {
        var pending = new List<PolledItemRef>();

        for (var index = 0; index < ordered.Count; index++)
        {
            var candidate = ordered[index];

            if (lastProcessedAtUtc is { } checkpointAt &&
                IsAtOrBefore(candidate, checkpointAt, lastProcessedKey))
            {
                continue;
            }

            pending.Add(new PolledItemRef(
                index, candidate.Key, ReportMailIdentity.ForS3(candidate.Key)));
        }

        return pending;
    }

    private static bool IsAtOrBefore(S3ObjectRef candidate, DateTime checkpointAtUtc, string? checkpointKey)
    {
        if (candidate.LastModifiedUtc != checkpointAtUtc)
        {
            return candidate.LastModifiedUtc < checkpointAtUtc;
        }

        // Same timestamp: the key breaks the tie, in the same ordinal order the listing was
        // sorted by. A null key means a checkpoint written before the tiebreaker existed, so
        // the whole second is treated as done rather than replayed.
        return checkpointKey is null || string.CompareOrdinal(candidate.Key, checkpointKey) <= 0;
    }

    /// <returns>
    /// What this pass saw, and where the next pass's listing should resume — null when this
    /// pass reached the natural end of the prefix (a lap completed, so the next one starts
    /// over from the top), non-null when it stopped early on the per-pass cap (there is more
    /// of the prefix past what this pass saw).
    /// </returns>
    private async Task<(IReadOnlyList<S3ObjectRef> Objects, string? NextCursorKey)> ListAsync(
        IAmazonS3 client, ReportSource source, string? cursorKey, CancellationToken ct)
    {
        var objects = new List<S3ObjectRef>();
        var request = new ListObjectsV2Request
        {
            BucketName = source.S3Bucket,
            Prefix = string.IsNullOrWhiteSpace(source.S3Prefix) ? null : source.S3Prefix,

            // Only takes effect on the request that has no ContinuationToken yet — exactly
            // the first page below, which is what resuming a lap-in-progress needs.
            StartAfter = string.IsNullOrEmpty(cursorKey) ? null : cursorKey,

            // Capped to the per-pass budget too (S3's own maximum is 1000 regardless), or a
            // single page could return more than the cap before the check below ever runs —
            // the cap would still hold on average but could overshoot it by up to 999 keys.
            MaxKeys = Math.Min(_maxKeysPerPass, 1000),
        };

        var stoppedOnCap = false;

        do
        {
            ct.ThrowIfCancellationRequested();

            var response = await client.ListObjectsV2Async(request, ct);

            foreach (var item in response.S3Objects ?? [])
            {
                // A "directory marker" — a zero-byte key ending in a slash, which a console
                // creates when you make a folder. Fetching one yields nothing and would count
                // as a parse failure on every pass.
                if (item.Key.EndsWith('/') || item.Size is null or 0)
                {
                    continue;
                }

                objects.Add(new S3ObjectRef(
                    item.Key, (item.LastModified ?? DateTime.UtcNow).ToUniversalTime()));
            }

            if (objects.Count >= _maxKeysPerPass)
            {
                stoppedOnCap = true;
                logger.LogWarning(
                    "Stopped listing {Bucket} for report source {ReportSourceId} at {Count} objects " +
                    "this pass, resuming after {ResumeKey} next time. Set a narrower prefix if this " +
                    "bucket holds more than reports.",
                    source.S3Bucket, source.Id, objects.Count, objects[^1].Key);
                break;
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (!string.IsNullOrEmpty(request.ContinuationToken));

        return (objects, stoppedOnCap ? objects[^1].Key : null);
    }

    /// <summary>
    /// What one eligible object archived under: its own Date header if it is a message-shaped
    /// object that has one, otherwise its last-modified date. A ranged fetch of the first
    /// <see cref="PolledObjectContent.SniffBytes"/>, not the whole object — enough to sniff and
    /// read a header block, at a fraction of the cost of downloading what may be a large
    /// attachment.
    /// </summary>
    private static async Task<DateTime> ResolveArchivedAtUtcAsync(
        IAmazonS3 client, string bucket, S3ObjectRef obj, CancellationToken ct)
    {
        GetObjectResponse response;

        try
        {
            response = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = obj.Key,
                ByteRange = new ByteRange(0, PolledObjectContent.SniffBytes - 1),
            }, ct);
        }
        catch (AmazonS3Exception)
        {
            // Deleted or made unreadable between the listing and this request. Falling back
            // to last-modified is the same answer this object would have gotten before this
            // lookup existed, not a new failure mode.
            return obj.LastModifiedUtc;
        }

        using (response)
        {
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);

            return PolledObjectContent.TryReadOwnDateUtc(buffer.ToArray()) ?? obj.LastModifiedUtc;
        }
    }

    /// <summary>
    /// Builds the client for one source. Not shared with the backup client on purpose: that
    /// one is a singleton over install-wide configuration, and the whole point of these
    /// credentials living on the row is that two sources may be two different accounts.
    /// </summary>
    private static AmazonS3Client CreateClient(ReportSource source, string secret)
    {
        var config = new AmazonS3Config { ForcePathStyle = source.S3ForcePathStyle };
        var region = string.IsNullOrWhiteSpace(source.S3Region) ? "us-east-1" : source.S3Region;

        if (!string.IsNullOrWhiteSpace(source.S3Endpoint))
        {
            // ServiceURL and RegionEndpoint are mutually exclusive in the SDK; setting both
            // throws, so an explicit endpoint wins and the region is only carried as the
            // signing region. Same rule as the backup client.
            config.ServiceURL = source.S3Endpoint;
            config.AuthenticationRegion = region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        // No key means the ambient chain — an instance role, IRSA, a shared profile. That is
        // the better credential where it exists, so an empty username is a configuration
        // rather than an omission.
        return string.IsNullOrWhiteSpace(source.Username) || string.IsNullOrWhiteSpace(secret)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(source.Username, secret, config);
    }

    private sealed class S3ReadSession(
        IAmazonS3 client,
        string bucket,
        IReadOnlyList<S3ObjectRef> ordered,
        IReadOnlyList<PolledItemRef> pending,
        long maxMessageBytes) : IPolledReadSession
    {
        public IReadOnlyList<PolledItemRef> Pending => pending;

        /// <summary>
        /// Answered on every sync, unlike IMAP. The listing the pass already made carries
        /// every object's last-modified date, so the oldest one is free.
        /// </summary>
        public DateTime? OldestMessageAtUtc => ordered.Count == 0 ? null : ordered[0].LastModifiedUtc;

        public async Task<MimeMessage> FetchAsync(PolledItemRef item, CancellationToken ct)
        {
            using var response = await client.GetObjectAsync(
                new GetObjectRequest { BucketName = bucket, Key = item.Identity }, ct);

            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);

            return PolledObjectContent.ToMessage(
                buffer.ToArray(),
                item.Identity,
                (response.LastModified ?? ordered[(int)item.Token].LastModifiedUtc).ToUniversalTime(),
                maxMessageBytes);
        }

        /// <summary>Nothing to record. A bucket has no generation.</summary>
        public void ApplyGeneration(ReportSource source)
        {
        }

        public void ApplyCheckpoint(ReportSource source, PolledItemRef handled)
        {
            source.LastProcessedObjectAtUtc = ordered[(int)handled.Token].LastModifiedUtc;
            source.LastProcessedObjectKey = handled.Identity;
        }

        /// <summary>Nothing to close. Each request stands alone; there is no session to end.</summary>
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class S3PruneSession(
        IAmazonS3 client,
        string bucket,
        IReadOnlyList<S3ObjectRef> eligible,
        IReadOnlyList<DateTime> archivedAtUtc,
        IReadOnlyList<S3ObjectRef> survivors,
        bool dryRun,
        ILogger logger) : IPolledPruneSession
    {
        private readonly HashSet<int> _deleted = [];

        public IReadOnlyList<PolledPruneCandidate> Eligible { get; } =
            [.. eligible.Select((x, index) => new PolledPruneCandidate(
                index, archivedAtUtc[index], ReportMailIdentity.ForS3(x.Key)))];

        public async Task DeleteAsync(PolledPruneCandidate candidate, CancellationToken ct)
        {
            if (dryRun)
            {
                return;
            }

            var key = eligible[(int)candidate.Token].Key;
            await client.DeleteObjectAsync(bucket, key, ct);
            _deleted.Add((int)candidate.Token);

            logger.LogDebug("Deleted {Bucket}/{Key} past the retention cutoff", bucket, key);
        }

        /// <summary>
        /// Nothing to do, and worth saying why rather than leaving an empty method. S3 has no
        /// two-phase delete: <c>DeleteObject</c> is effective when it returns, so by the time
        /// this is called the objects are already gone. The mail protocols are the odd ones
        /// here, not this one.
        /// </summary>
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Computed from the listing this pass already made, minus what it deleted, rather
        /// than by listing again — the answer is fully determined, and a second round trip
        /// could only disagree with it.
        /// </summary>
        public Task<DateTime?> GetOldestMessageAtUtcAsync(CancellationToken ct)
        {
            var remaining = eligible
                .Where((_, index) => !_deleted.Contains(index))
                .Concat(survivors)
                .Select(x => x.LastModifiedUtc)
                .ToArray();

            return Task.FromResult(remaining.Length == 0 ? null : (DateTime?)remaining.Min());
        }

        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
