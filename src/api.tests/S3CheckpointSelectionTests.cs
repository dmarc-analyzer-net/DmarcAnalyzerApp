using DmarcAnalyzer.Api.Application.Ingestion;
using Xunit;
using static DmarcAnalyzer.Api.Application.Ingestion.S3ReportSourceTransport;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Where an S3 pass resumes — the third answer to the same question IMAP and POP3 each answer
/// differently, and the one with the least help from the protocol.
/// <para>
/// S3's own resume primitive is <c>StartAfter</c>, which works on key order. Using it would be
/// the obvious implementation and it is the reason these tests exist: nothing makes an
/// object's key sort in the order it arrived, so a bucket whose keys carry a hashed or random
/// prefix would have every new object that sorted below the checkpoint skipped — permanently,
/// with no error anywhere. The checkpoint is a (last-modified, key) pair instead, and the
/// cases below are the ones that pair has to get right.
/// </para>
/// </summary>
public sealed class S3CheckpointSelectionTests
{
    private static DateTime At(int minute) => new(2026, 8, 1, 6, minute, 0, DateTimeKind.Utc);

    private static IReadOnlyList<S3ObjectRef> Bucket(params S3ObjectRef[] objects) => Order(objects);

    private static S3ObjectRef Obj(string key, int minute) => new(key, At(minute));

    [Fact]
    public void WithNoCheckpointEverythingIsSelected()
    {
        var pending = SelectObjectsPastCheckpoint(
            Bucket(Obj("b", 2), Obj("a", 1)), null, null);

        Assert.Equal(["a", "b"], pending.Select(x => x.Identity));
    }

    [Fact]
    public void OldestFirstRegardlessOfKeyOrder()
    {
        var pending = SelectObjectsPastCheckpoint(
            Bucket(Obj("zzz", 1), Obj("aaa", 3), Obj("mmm", 2)), null, null);

        Assert.Equal(["zzz", "mmm", "aaa"], pending.Select(x => x.Identity));
    }

    [Fact]
    public void ACheckpointAtTheNewestObjectSelectsNothing()
    {
        var pending = SelectObjectsPastCheckpoint(
            Bucket(Obj("a", 1), Obj("b", 2)), At(2), "b");

        Assert.Empty(pending);
    }

    /// <summary>
    /// The bug this design exists to avoid, stated as a test. The new object's key sorts
    /// below the checkpoint's, so a <c>StartAfter</c> resume would never return it — and
    /// nothing would ever notice, because a bucket that quietly withholds an object looks
    /// exactly like a bucket with nothing new in it.
    /// </summary>
    [Fact]
    public void ANewerObjectWhoseKeySortsBelowTheCheckpointIsStillSelected()
    {
        var pending = SelectObjectsPastCheckpoint(
            Bucket(Obj("zzz-old", 1), Obj("aaa-new", 2)), At(1), "zzz-old");

        Assert.Equal(["aaa-new"], pending.Select(x => x.Identity));
    }

    /// <summary>
    /// A bulk upload stamps many objects on the same second. Selecting on the timestamp alone
    /// would either skip every sibling of the checkpointed object or replay all of them on
    /// every pass, for ever; the key breaks the tie.
    /// </summary>
    [Fact]
    public void ObjectsSharingATimestampAreSplitByKey()
    {
        var pending = SelectObjectsPastCheckpoint(
            Bucket(Obj("a", 1), Obj("b", 1), Obj("c", 1)), At(1), "a");

        Assert.Equal(["b", "c"], pending.Select(x => x.Identity));
    }

    [Fact]
    public void TheCheckpointedObjectItselfIsNotReprocessed()
    {
        var pending = SelectObjectsPastCheckpoint(
            Bucket(Obj("a", 1), Obj("b", 1)), At(1), "b");

        Assert.Empty(pending);
    }

    /// <summary>
    /// Unlike the POP3 checkpoint, this one survives its own object being deleted: a pair is a
    /// position in an ordering rather than a name that has to resolve. That matters because
    /// retention deletion is exactly the thing that removes objects behind the checkpoint.
    /// </summary>
    [Fact]
    public void ACheckpointWhoseObjectIsGoneStillResumesInTheRightPlace()
    {
        var pending = SelectObjectsPastCheckpoint(
            Bucket(Obj("c", 3), Obj("d", 4)), At(2), "b-since-deleted");

        Assert.Equal(["c", "d"], pending.Select(x => x.Identity));
    }

    /// <summary>
    /// A checkpoint written before the key tiebreaker existed has a timestamp and no key.
    /// Treating that whole second as done risks skipping a sibling once; treating it as
    /// pending would replay it on every pass. Once is the recoverable one, and deduplication
    /// covers it.
    /// </summary>
    [Fact]
    public void ATimestampOnlyCheckpointTreatsThatSecondAsDone()
    {
        var pending = SelectObjectsPastCheckpoint(
            Bucket(Obj("a", 1), Obj("b", 1), Obj("c", 2)), At(1), null);

        Assert.Equal(["c"], pending.Select(x => x.Identity));
    }

    /// <summary>
    /// The token is an index into the ordered listing, which is how the session gets back to
    /// an object's timestamp when it checkpoints — the key alone would not be enough.
    /// </summary>
    [Fact]
    public void TokensIndexIntoTheOrderedListing()
    {
        var ordered = Bucket(Obj("a", 1), Obj("b", 2), Obj("c", 3));

        var pending = SelectObjectsPastCheckpoint(ordered, At(1), "a");

        Assert.Equal([1L, 2L], pending.Select(x => x.Token));
        Assert.Equal("b", ordered[(int)pending[0].Token].Key);
    }

    [Fact]
    public void AnEmptyBucketSelectsNothing()
        => Assert.Empty(SelectObjectsPastCheckpoint(Bucket(), At(1), "a"));
}
