using DmarcAnalyzer.Api.Application.Ingestion;
using MailKit;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The checkpoint advance, which had a bug that only appears once a mailbox is fully caught up.
/// <para>
/// The sync searches <c>{checkpoint+1}:*</c> so the server does not send every UID in the
/// mailbox. IMAP resolves <c>*</c> to the highest UID that exists, so when the checkpoint is
/// already at the newest message that range is normalised and returns that message again. The
/// service recomputed the checkpoint it already had, saved no change, and repeated on the next
/// poll — one message re-fetched and re-parsed every 16 seconds, 5,162 times on a real
/// instance, with a sync-run row for each.
/// </para>
/// </summary>
public sealed class MailboxUidSelectionTests
{
    private static UniqueId[] Uids(params uint[] ids) => [.. ids.Select(x => new UniqueId(x))];

    /// <summary>
    /// The regression. A search that hands back the message at the checkpoint must select
    /// nothing, or the checkpoint can never move past it.
    /// </summary>
    [Fact]
    public void AUidAtTheCheckpointIsNotReprocessed()
    {
        var selected = MailboxSyncService.SelectUidsToProcess(Uids(230686), 230686, 500);

        Assert.Empty(selected);
    }

    [Fact]
    public void UidsBelowTheCheckpointAreIgnoredToo()
    {
        var selected = MailboxSyncService.SelectUidsToProcess(Uids(10, 200, 230686), 230686, 500);

        Assert.Empty(selected);
    }

    [Fact]
    public void OnlyWhatIsPastTheCheckpointIsSelected()
    {
        var selected = MailboxSyncService.SelectUidsToProcess(Uids(230686, 230687, 230688), 230686, 500);

        Assert.Equal([230687u, 230688u], selected.Select(x => x.Id));
    }

    /// <summary>A first sync has no checkpoint, so everything is fair game.</summary>
    [Fact]
    public void WithNoCheckpointEverythingIsSelected()
    {
        var selected = MailboxSyncService.SelectUidsToProcess(Uids(3, 1, 2), null, 500);

        Assert.Equal([1u, 2u, 3u], selected.Select(x => x.Id));
    }

    /// <summary>
    /// Oldest first, and explicitly so: the batch cap only means "the oldest N" if the order is
    /// known, and the unlimited backfill is defined as oldest-to-newest.
    /// </summary>
    [Fact]
    public void SelectionIsOldestFirstEvenIfTheServerIsNot()
    {
        var selected = MailboxSyncService.SelectUidsToProcess(Uids(9, 5, 7, 6), 4, 2);

        Assert.Equal([5u, 6u], selected.Select(x => x.Id));
    }

    [Fact]
    public void TheBatchSizeCaps()
    {
        var selected = MailboxSyncService.SelectUidsToProcess(Uids(1, 2, 3, 4, 5), null, 3);

        Assert.Equal(3, selected.Length);
    }

    /// <summary>A batch size of zero or less must still make progress, not stall silently.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveBatchSizeStillTakesOne(int batchSize)
    {
        var selected = MailboxSyncService.SelectUidsToProcess(Uids(1, 2, 3), null, batchSize);

        Assert.Single(selected);
        Assert.Equal(1u, selected[0].Id);
    }

    [Fact]
    public void AnEmptySearchSelectsNothing()
        => Assert.Empty(MailboxSyncService.SelectUidsToProcess([], 230686, 500));
}
