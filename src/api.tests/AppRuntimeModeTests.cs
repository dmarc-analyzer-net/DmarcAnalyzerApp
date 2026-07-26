using DmarcAnalyzer.Api.Application.Hosting;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// <c>APP_MODE</c> decides whether a container serves traffic, ingests reports,
/// or both. Getting it wrong is unusually hard to notice — a machine that serves
/// the console but runs no loop passes every check an operator makes — so the
/// parse is strict rather than forgiving.
/// </summary>
public sealed class AppRuntimeModeTests
{
    [Theory]
    [InlineData("api", AppMode.Api)]
    [InlineData("worker", AppMode.Worker)]
    [InlineData("all", AppMode.All)]
    public void ParsesEachDocumentedMode(string value, AppMode expected)
        => Assert.Equal(expected, AppRuntimeMode.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetMeansApi(string? value)
    {
        // The Dockerfile's default, and what most deployments never set.
        Assert.Equal(AppMode.Api, AppRuntimeMode.Parse(value));
    }

    [Theory]
    [InlineData("API", AppMode.Api)]
    [InlineData("Worker", AppMode.Worker)]
    [InlineData("  All  ", AppMode.All)]
    public void CaseAndSurroundingSpaceDoNotMatter(string value, AppMode expected)
        => Assert.Equal(expected, AppRuntimeMode.Parse(value));

    [Theory]
    [InlineData("woker")]      // the typo this exists to catch
    [InlineData("both")]       // a plausible guess at the combined mode's name
    [InlineData("combined")]   // ditto — the ADR calls it this in prose
    [InlineData("api,worker")] // treating it like a list
    [InlineData("true")]
    public void AnythingElseFailsStartup(string value)
    {
        // Deliberately not a fallback to api. A container that serves traffic and
        // ingests nothing looks healthy from every angle: it is up, the UI loads,
        // the healthcheck passes. One loud crash is far cheaper to diagnose.
        var ex = Assert.Throws<InvalidOperationException>(() => AppRuntimeMode.Parse(value));

        Assert.Contains(value, ex.Message);
        Assert.Contains("api, worker, all", ex.Message);
    }

    [Fact]
    public void WorkerRunsInWorkerAndAllOnly()
    {
        Assert.False(AppMode.Api.RunsWorker());
        Assert.True(AppMode.Worker.RunsWorker());
        Assert.True(AppMode.All.RunsWorker());
    }

    [Fact]
    public void HttpIsServedInApiAndAllOnly()
    {
        Assert.True(AppMode.Api.RunsHttp());
        Assert.False(AppMode.Worker.RunsHttp());
        Assert.True(AppMode.All.RunsHttp());
    }

    [Fact]
    public void EveryModeIsReachableFromItsDocumentedName()
    {
        // Guards the docs and the error message against a mode being added to the
        // enum without a name, or renamed on one side only.
        var parsed = AppRuntimeMode.Names.Select(AppRuntimeMode.Parse).ToArray();

        Assert.Equal(Enum.GetValues<AppMode>().Order(), parsed.Order());
    }
}
