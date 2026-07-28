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
        var selected = MailboxSyncService.SelectUidsPastCheckpoint(Uids(230686), 230686);

        Assert.Empty(selected);
    }

    [Fact]
    public void UidsBelowTheCheckpointAreIgnoredToo()
    {
        var selected = MailboxSyncService.SelectUidsPastCheckpoint(Uids(10, 200, 230686), 230686);

        Assert.Empty(selected);
    }

    [Fact]
    public void OnlyWhatIsPastTheCheckpointIsSelected()
    {
        var selected = MailboxSyncService.SelectUidsPastCheckpoint(Uids(230686, 230687, 230688), 230686);

        Assert.Equal([230687u, 230688u], selected.Select(x => x.Id));
    }


    /// <summary>A first sync has no checkpoint, so everything the search found is fair game.</summary>
    [Fact]
    public void WithNoCheckpointEverythingIsSelected()
    {
        var selected = MailboxSyncService.SelectUidsPastCheckpoint(Uids(3, 1, 2), null);

        Assert.Equal([1u, 2u, 3u], selected.Select(x => x.Id));
    }

    [Fact]
    public void AnEmptySearchSelectsNothing()
        => Assert.Empty(MailboxSyncService.SelectUidsPastCheckpoint([], 230686));

    /// <summary>
    /// Oldest first, explicitly. The drain loop's batch boundaries and the oldest-to-newest
    /// backfill both depend on the order, so it is not left to whatever the server returned.
    /// </summary>
    [Fact]
    public void SelectionIsOldestFirstEvenIfTheServerIsNot()
    {
        var selected = MailboxSyncService.SelectUidsPastCheckpoint(Uids(9, 5, 7, 6), 4);

        Assert.Equal([5u, 6u, 7u, 9u], selected.Select(x => x.Id));
    }
}
