using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DmarcAnalyzer.Api.Application.Auth;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Keeps <c>docs/ops/configuration.md</c> true.
/// <para>
/// The promise made to self-hosters is that one set of environment variables
/// means the same thing in every deployment shape — both Compose overlays and
/// the Kubernetes chart. A document alone cannot hold that promise: it is
/// accurate the day it is written and then a setting gets added to an options
/// class, or renamed, and nothing notices until someone sets a variable that
/// silently does nothing.
/// </para>
/// <para>
/// So the contract is asserted from three directions: every bound setting is
/// documented, every documented setting still exists, and every options class is
/// accounted for. The third is what stops the first two being circumvented by
/// adding a whole new section nobody registered.
/// </para>
/// </summary>
public sealed class ConfigurationContractTests
{
    /// <summary>Config sections bound to a strongly-typed options class.</summary>
    private static readonly (string Section, Type Type)[] BoundSections =
    [
        ("Worker", typeof(DmarcAnalyzer.Api.Workers.WorkerOptions)),
        ("Email", typeof(DmarcAnalyzer.Api.Application.Notifications.EmailOptions)),
        ("Alerts", typeof(DmarcAnalyzer.Api.Application.Notifications.AlertOptions)),
        ("Digest", typeof(DmarcAnalyzer.Api.Application.Notifications.DigestOptions)),
        ("Dns", typeof(DmarcAnalyzer.Api.Application.Analytics.DnsOptions)),
        ("Retention", typeof(DmarcAnalyzer.Api.Application.Retention.RetentionOptions)),
        ("Network", typeof(DmarcAnalyzer.Api.Application.Security.NetworkOptions)),
        ("Auth:Oidc", typeof(OidcOptions)),
    ];

    /// <summary>
    /// Settings read straight from <c>IConfiguration</c> rather than through an
    /// options class, so reflection cannot find them.
    /// </summary>
    private static readonly string[] LooseSettings =
    [
        "Security__CredentialEncryptionKey",
        "ConnectionStrings__Default",
        "Database__MigrateOnStartup",
        "APP_MODE",
    ];

    /// <summary>
    /// Types ending in "Options" that are not configuration sections, with the
    /// reason. Anything not listed here must appear in <see cref="BoundSections"/>.
    /// </summary>
    private static readonly Dictionary<string, string> NotConfigurationSections = new()
    {
        ["ForwardedHeadersSetup"] = "static helper, not an options class",
    };

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

    private static string DocPath() => Path.Combine(RepoRoot(), "docs", "ops", "configuration.md");

    /// <summary>
    /// Every `Backticked__Token` in the document. Indexed list entries
    /// (`Network__TrustedProxies__0`) collapse to their base name, because the
    /// index is a value position rather than part of the setting's identity.
    /// </summary>
    private static HashSet<string> DocumentedVariables()
    {
        var text = File.ReadAllText(DocPath());
        var tokens = Regex.Matches(text, @"`([A-Za-z][A-Za-z0-9_.]*)`")
            .Select(m => m.Groups[1].Value)
            .Where(v => v.Contains("__") || v == "APP_MODE" || v == "AllowedHosts")
            .Select(v => Regex.Replace(v, @"__\d+$", ""));

        return [.. tokens];
    }

    private static string EnvName(string section, string property)
        => $"{section.Replace(':', '_').Replace("_", "__")}__{property}";

    public static TheoryData<string, string> EveryBoundSetting()
    {
        var data = new TheoryData<string, string>();
        foreach (var (section, type) in BoundSections)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetSetMethod() is null)
                {
                    continue; // computed, not bindable
                }

                data.Add(section, property.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryBoundSetting))]
    public void EverySettingInCodeIsDocumented(string section, string property)
    {
        var variable = EnvName(section, property);

        Assert.True(
            DocumentedVariables().Contains(variable),
            $"{variable} is bound from configuration but missing from docs/ops/configuration.md. " +
            $"Add a row for it, or the promise that Compose and Kubernetes take the same settings " +
            $"stops being true for this one.");
    }

    [Fact]
    public void SettingsReadOutsideAnOptionsClassAreDocumented()
    {
        // These have no property to reflect over, so nothing else would catch them.
        var documented = DocumentedVariables();
        var missing = LooseSettings.Where(s => !documented.Contains(s)).ToArray();

        Assert.True(missing.Length == 0, $"Undocumented: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryDocumentedSettingStillExists()
    {
        // The other direction: catches a row left behind after a rename, which
        // sends an operator to set a variable that does nothing.
        var real = new HashSet<string>(LooseSettings)
        {
            "AllowedHosts",
            "Logging__LogLevel__Default",
            "Logging__LogLevel__Microsoft.AspNetCore",
            "ASPNETCORE_ENVIRONMENT",
            "ASPNETCORE_URLS",
        };

        foreach (var (section, type) in BoundSections)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                real.Add(EnvName(section, property.Name));
            }
        }

        // DMARC_* and COMPOSE_FILE are Compose-side conveniences the app never
        // reads; the document says so explicitly, in its own table.
        var stale = DocumentedVariables()
            .Where(v => !real.Contains(v))
            .Where(v => !v.StartsWith("DMARC_") && v != "COMPOSE_FILE" && v != "POSTGRES_PASSWORD")
            .ToArray();

        Assert.True(stale.Length == 0,
            $"Documented but no longer bound anywhere: {string.Join(", ", stale)}. " +
            $"Remove the row, or restore the setting.");
    }

    [Fact]
    public void EveryOptionsClassIsAccountedFor()
    {
        // Without this, adding a whole new configuration section and forgetting to
        // register it here would pass every other test in this file.
        var registered = BoundSections.Select(b => b.Type.Name).ToHashSet();

        var unregistered = typeof(OidcOptions).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true })
            .Where(t => t.Name.EndsWith("Options", StringComparison.Ordinal))
            .Where(t => !registered.Contains(t.Name))
            .Where(t => !NotConfigurationSections.ContainsKey(t.Name))
            .Select(t => t.Name)
            .ToArray();

        Assert.True(unregistered.Length == 0,
            $"Options class not in BoundSections: {string.Join(", ", unregistered)}. " +
            $"Add it with its section name, or list it in NotConfigurationSections with a reason.");
    }

    [Fact]
    public void AppsettingsDefaultsMatchTheDocumentedSections()
    {
        // appsettings.json is where a reader looks for defaults. A section present
        // there but absent from the doc means a whole group of settings is
        // undiscoverable to anyone deploying from environment variables.
        using var json = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "api", "appsettings.json")));

        var documented = DocumentedVariables();
        var undocumented = new List<string>();

        foreach (var section in json.RootElement.EnumerateObject())
        {
            if (section.Value.ValueKind != JsonValueKind.Object)
            {
                continue; // scalars such as AllowedHosts, checked above
            }

            foreach (var leaf in Leaves(section.Name, section.Value))
            {
                if (!documented.Contains(leaf))
                {
                    undocumented.Add(leaf);
                }
            }
        }

        Assert.True(undocumented.Count == 0,
            $"In appsettings.json but not documented: {string.Join(", ", undocumented)}");
    }

    private static IEnumerable<string> Leaves(string prefix, JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            var name = $"{prefix}__{property.Name}";

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in Leaves(name, property.Value))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return name;
            }
        }
    }
}
