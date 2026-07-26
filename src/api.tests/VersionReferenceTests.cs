using System.Text.RegularExpressions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Keeps the version numbers written into documentation from falling behind the
/// chart.
/// <para>
/// This is not cosmetic. <c>values.image.tag</c> defaults to the chart's
/// <c>appVersion</c>, so a stale <c>Chart.yaml</c> means anyone running
/// <c>helm install ./deploy/helm/dmarc-analyzer</c> from a clone silently
/// deploys the *previous* release — including its schema. That happened between
/// 0.1.0 and 0.2.0 and nothing caught it, because every command still worked and
/// every version referenced still existed.
/// </para>
/// <para>
/// A release-checklist item would not have caught it either; the checklist in
/// this repo had already drifted on the container count. So it is a test.
/// </para>
/// </summary>
public sealed class VersionReferenceTests
{
    /// <summary>
    /// Files whose version references must track the chart. The website has its
    /// own copies and lives in another repository — release.md carries that
    /// hand-off, since nothing here can check across the boundary.
    /// </summary>
    private static readonly string[] TrackedDocs =
    [
        "README.md",
        Path.Combine("deploy", "helm", "dmarc-analyzer", "README.md"),
    ];

    /// <summary>
    /// Version-shaped strings that are deliberately historical and must not be
    /// bumped: semver illustrations, and links to past releases.
    /// </summary>
    private static readonly Regex[] AllowedHistorical =
    [
        new(@"releases/tag/v\d+\.\d+\.\d+", RegexOptions.Compiled),
        new(@"`\d+\.\d+\.\d+`\s*→\s*`\d+\.\d+\.\d+`", RegexOptions.Compiled),
        new(@"\(`\d+\.\d+\.\d+`\s*→", RegexOptions.Compiled),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DmarcAnalyzerApp.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (string Version, string AppVersion) ChartVersions()
    {
        var text = File.ReadAllText(Path.Combine(
            RepoRoot(), "deploy", "helm", "dmarc-analyzer", "Chart.yaml"));

        string Read(string key) =>
            Regex.Match(text, $@"^{key}:\s*""?(\d+\.\d+\.\d+)""?\s*$", RegexOptions.Multiline)
                .Groups[1].Value;

        return (Read("version"), Read("appVersion"));
    }

    [Fact]
    public void ChartVersionAndAppVersionAgree()
    {
        // A release tag sets both from the tag, so they are equal by construction
        // in anything published. Keeping the committed values equal too means
        // "which app does chart X deploy" has one answer from a clone as well.
        var (version, appVersion) = ChartVersions();

        Assert.False(string.IsNullOrEmpty(version), "Could not parse version from Chart.yaml");
        Assert.Equal(version, appVersion);
    }

    [Fact]
    public void CloneInstallDoesNotDeployAnOlderImage()
    {
        // The specific bug this file exists for.
        var (_, appVersion) = ChartVersions();
        var values = File.ReadAllText(Path.Combine(
            RepoRoot(), "deploy", "helm", "dmarc-analyzer", "values.yaml"));

        // An empty image.tag means "use appVersion", which is the intended default.
        var tag = Regex.Match(values, @"^\s{2}tag:\s*""(.*)""\s*$", RegexOptions.Multiline).Groups[1].Value;

        Assert.True(
            tag.Length == 0 || tag == appVersion,
            $"values.image.tag is \"{tag}\" but the chart's appVersion is {appVersion}. " +
            $"Leave the tag empty so it follows appVersion, or keep the two in step — otherwise " +
            $"`helm install ./deploy/helm/dmarc-analyzer` deploys an image nobody intended.");
    }

    public static TheoryData<string> Docs()
    {
        var data = new TheoryData<string>();
        foreach (var doc in TrackedDocs)
        {
            data.Add(doc);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Docs))]
    public void DocsDoNotRecommendAnOlderVersion(string relativePath)
    {
        var (chartVersion, _) = ChartVersions();
        var chart = Version.Parse(chartVersion);
        var path = Path.Combine(RepoRoot(), relativePath);

        Assert.True(File.Exists(path), $"{relativePath} is tracked here but missing");

        var stale = new List<string>();

        foreach (var (line, number) in File.ReadLines(path).Select((l, i) => (l, i + 1)))
        {
            if (AllowedHistorical.Any(r => r.IsMatch(line)))
            {
                continue;
            }

            foreach (Match m in Regex.Matches(line, @"\b(\d+\.\d+\.\d+)\b"))
            {
                if (Version.TryParse(m.Groups[1].Value, out var found) && found < chart)
                {
                    stale.Add($"{relativePath}:{number} references {found}, chart is {chart}");
                }
            }
        }

        Assert.True(stale.Count == 0,
            "Documentation still points at a superseded release:\n  " +
            string.Join("\n  ", stale) +
            "\n\nBump these with the chart, or add the pattern to AllowedHistorical if the " +
            "reference is deliberately about a past version.");
    }
}
