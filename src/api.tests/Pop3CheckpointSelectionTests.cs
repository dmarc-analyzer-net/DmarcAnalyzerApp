using DmarcAnalyzer.Api.Application.Ingestion;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Where a POP3 pass resumes. This is the counterpart of <see cref="MailboxUidSelectionTests"/>
/// and it is a different problem: IMAP compares an ordered UID against a checkpoint, POP3 has
/// nothing to compare — a UIDL is opaque — so resuming means locating the checkpoint in the
/// listing and taking what follows it.
/// <para>
/// The failure that matters most here is the one that looks like success. A checkpoint sitting
/// at the last entry must select nothing; getting that wrong is how the IMAP path once
/// re-fetched the same message every 16 seconds for 5,162 passes, and POP3 has no server-side
/// range to blame it on.
/// </para>
/// </summary>
public sealed class Pop3CheckpointSelectionTests
{
    private static IReadOnlyList<string> Listing(params string[] uidls) => uidls;

    [Fact]
    public void ACheckpointAtTheLastMessageSelectsNothing()
    {
        var pending = MailboxSyncService.SelectUidlsPastCheckpoint(
            Listing("aaa", "bbb", "ccc"), "ccc");

        Assert.Empty(pending);
    }

    [Fact]
    public void OnlyWhatFollowsTheCheckpointIsSelected()
    {
        var pending = MailboxSyncService.SelectUidlsPastCheckpoint(
            Listing("aaa", "bbb", "ccc", "ddd"), "bbb");

        Assert.Equal(["ccc", "ddd"], pending.Select(x => x.Identity));
    }

    /// <summary>
    /// The listing is what defines the order, so the pass has to keep it. Its own batch
    /// boundaries and the oldest-to-newest backfill both assume the message after this one is
    /// the next one along.
    /// </summary>
    [Fact]
    public void ListingOrderIsPreserved()
    {
        var pending = MailboxSyncService.SelectUidlsPastCheckpoint(
            Listing("zzz", "aaa", "mmm"), null);

        Assert.Equal(["zzz", "aaa", "mmm"], pending.Select(x => x.Identity));
    }

    /// <summary>
    /// The token is a position in <em>this</em> listing, because that is the only way POP3
    /// lets a message be fetched. It is not the UIDL and it is not stable across sessions.
    /// </summary>
    [Fact]
    public void TokensAreListingPositionsNotUidls()
    {
        var pending = MailboxSyncService.SelectUidlsPastCheckpoint(
            Listing("aaa", "bbb", "ccc"), "aaa");

        Assert.Equal([1L, 2L], pending.Select(x => x.Token));
    }

    [Fact]
    public void WithNoCheckpointEverythingIsSelected()
    {
        var pending = MailboxSyncService.SelectUidlsPastCheckpoint(
            Listing("aaa", "bbb"), null);

        Assert.Equal(["aaa", "bbb"], pending.Select(x => x.Identity));
    }

    /// <summary>
    /// A checkpoint naming a message that is no longer in the mailbox — deleted by hand, or
    /// by another client reading the same mailbox — leaves no position to recover, so the
    /// whole listing is pending again. Deduplication is what makes that merely expensive; the
    /// alternative, guessing a position, would skip real reports.
    /// </summary>
    [Fact]
    public void AMissingCheckpointFallsBackToTheWholeMailbox()
    {
        var pending = MailboxSyncService.SelectUidlsPastCheckpoint(
            Listing("aaa", "bbb"), "deleted-long-ago");

        Assert.Equal(["aaa", "bbb"], pending.Select(x => x.Identity));
    }

    /// <summary>
    /// UIDLs are case-sensitive octet strings under RFC 1939, so a case-folded match would
    /// resume at the wrong message on a server that issues both.
    /// </summary>
    [Fact]
    public void CheckpointMatchingIsCaseSensitive()
    {
        var pending = MailboxSyncService.SelectUidlsPastCheckpoint(
            Listing("AAA", "bbb"), "aaa");

        Assert.Equal(["AAA", "bbb"], pending.Select(x => x.Identity));
    }

    [Fact]
    public void AnEmptyMailboxSelectsNothing()
    {
        Assert.Empty(MailboxSyncService.SelectUidlsPastCheckpoint(Listing(), "aaa"));
        Assert.Empty(MailboxSyncService.SelectUidlsPastCheckpoint(Listing(), null));
    }
}
