using DmarcAnalyzer.Api.Workers;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class QueueWorkerDelayTests
{
    private const int ProdInterval = 3600; // appsettings.json default
    private const int DevInterval = 15;    // appsettings.Development.json

    [Fact]
    public void HealthyPass_WaitsTheConfiguredInterval()
    {
        Assert.Equal(TimeSpan.FromSeconds(ProdInterval), QueueWorkerService.NextDelay(0, ProdInterval));
        Assert.Equal(TimeSpan.FromSeconds(DevInterval), QueueWorkerService.NextDelay(0, DevInterval));
    }

    [Fact]
    public void HealthyPass_NeverPollsFasterThanTheFloor()
    {
        // A misconfigured tiny interval must not turn into a hot loop.
        Assert.Equal(TimeSpan.FromSeconds(15), QueueWorkerService.NextDelay(0, 1));
        Assert.Equal(TimeSpan.FromSeconds(15), QueueWorkerService.NextDelay(0, 0));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    public void AfterFailure_RetriesSoonAndBacksOffExponentially(int failures, int expectedSeconds)
    {
        // The point of the fix: don't idle for the full hour after one transient
        // failure (e.g. the schema wasn't migrated yet when the worker started).
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), QueueWorkerService.NextDelay(failures, ProdInterval));
    }

    [Fact]
    public void Backoff_IsCappedAtTheConfiguredInterval()
    {
        // Persistent failure settles at the normal cadence rather than growing
        // without bound.
        Assert.Equal(TimeSpan.FromSeconds(ProdInterval), QueueWorkerService.NextDelay(50, ProdInterval));

        // With a short interval the cap binds almost immediately.
        Assert.Equal(TimeSpan.FromSeconds(DevInterval), QueueWorkerService.NextDelay(3, DevInterval));
    }

    [Fact]
    public void Backoff_DoesNotOverflow_AtAbsurdFailureCounts()
    {
        var delay = QueueWorkerService.NextDelay(int.MaxValue, ProdInterval);
        Assert.Equal(TimeSpan.FromSeconds(ProdInterval), delay);
        Assert.True(delay > TimeSpan.Zero);
    }
}
