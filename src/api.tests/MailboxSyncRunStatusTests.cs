using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Ingestion;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// A sync run that times out part-way through a backlog keeps its checkpoint, so the
/// next pass resumes instead of starting over. Recording that as <c>failed</c> would
/// read as "nothing happened" and would count the source against the failing-mailbox
/// tally on the dashboard, which counts only <c>failed</c>.
/// </summary>
public sealed class MailboxSyncRunStatusTests
{
    /// <summary>
    /// A stand-in for "the pass got this far". Only its presence matters to the status
    /// rule, not what protocol named it or what it is called.
    /// </summary>
    private static PolledItemRef Handled(uint uid)
        => new(uid, uid.ToString(), ReportMailIdentity.ForImap(uid, 9));

    [Fact]
    public void TimeoutWithProgressIsPartial()
    {
        Assert.Equal("partial",
            MailboxSyncService.ResolveUnsuccessfulRunStatus(new TimeoutException("budget"), Handled(4711)));
    }

    [Fact]
    public void CancellationWithProgressIsPartial()
    {
        // The linked token throws this one; the explicit budget check throws
        // TimeoutException. Both are the same event to an operator.
        Assert.Equal("partial",
            MailboxSyncService.ResolveUnsuccessfulRunStatus(new OperationCanceledException(), Handled(1)));
    }

    [Fact]
    public void TimeoutWithoutProgressIsFailed()
    {
        // Nothing was read, so there is no progress to report and nothing to resume
        // from — a source that cannot be reached at all looks exactly like this.
        Assert.Equal("failed",
            MailboxSyncService.ResolveUnsuccessfulRunStatus(new TimeoutException("budget"), null));
    }

    [Theory]
    [InlineData(42u)]
    [InlineData(null)]
    public void OtherFailuresStayFailedRegardlessOfProgress(uint? highestProcessedUid)
    {
        // An unexpected error is a failure whether or not it happened to get some way
        // in. Only running out of time earns "partial".
        Assert.Equal("failed",
            MailboxSyncService.ResolveUnsuccessfulRunStatus(
                new InvalidOperationException("broken"),
                highestProcessedUid.HasValue ? Handled(highestProcessedUid.Value) : null));
    }

    [Fact]
    public void IsTimeoutRecognisesBothCancellationShapes()
    {
        Assert.True(MailboxSyncService.IsTimeout(new TimeoutException()));
        Assert.True(MailboxSyncService.IsTimeout(new OperationCanceledException()));
        Assert.True(MailboxSyncService.IsTimeout(new TaskCanceledException()));
        Assert.False(MailboxSyncService.IsTimeout(new InvalidOperationException()));
    }
}
