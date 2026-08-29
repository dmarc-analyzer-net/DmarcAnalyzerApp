using DmarcAnalyzer.Api.Application.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The startup check that turns a mistyped setting into a sentence (#198).
/// <para>
/// Two properties matter and are asserted separately: it must reject exactly
/// what the configuration binder rejects — no more, or a working deployment
/// starts refusing to boot — and when it does reject, the message must contain
/// the things an operator needs, which is the variable in its double-underscore
/// spelling and what a valid value looks like.
/// </para>
/// </summary>
public sealed class ConfigurationPreflightTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void EmptyConfigurationPasses()
    {
        // Every section unset is the ordinary case — nothing to convert.
        ConfigurationPreflight.Validate(Configuration());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("True")]
    [InlineData("FALSE")]
    [InlineData(" true ")]
    public void AcceptsWhatTheBinderAccepts(string value)
    {
        // Surrounding whitespace is trimmed by the converter, so these bind
        // today and must keep binding.
        ConfigurationPreflight.Validate(Configuration(("Auth:Oidc:Enabled", value)));
    }

    [Fact]
    public void ExplainsAVariableSetToNothing()
    {
        // Counter-intuitive and worth its own test: the binder rejects an empty
        // string for a bool rather than treating the variable as unset, so
        // `Auth__Oidc__Enabled=` crashes where omitting the line entirely works.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationPreflight.Validate(Configuration(("Auth:Oidc:Enabled", ""))));

        Assert.Contains("Auth__Oidc__Enabled", ex.Message);
        Assert.Contains("not the same as leaving it unset", ex.Message);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("enabled")]
    public void RejectsValuesTheBinderCannotConvert(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationPreflight.Validate(Configuration(("Auth:Oidc:Enabled", value))));

        Assert.Contains("Auth__Oidc__Enabled", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
        Assert.Contains("true or false", ex.Message);
    }

    [Fact]
    public void NamesTheVariableInItsEnvironmentSpelling()
    {
        // The framework says 'Auth:Oidc:Enabled', which is not a string anyone
        // typed. The colon form appears nowhere in a Compose file or a Helm
        // values file, so the message has to translate it back.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationPreflight.Validate(Configuration(("Auth:Oidc:AutoProvision", "1"))));

        Assert.Contains("Auth__Oidc__AutoProvision", ex.Message);
        Assert.DoesNotContain("Auth:Oidc:AutoProvision", ex.Message);
    }

    [Fact]
    public void ExplainsAValueThatCarriesItsOwnQuotes()
    {
        // The trap behind the second half of #198: `- Auth__Oidc__Enabled="true"`
        // in a Compose list, or the same line in a --env-file, sets the value to
        // "true" with the quote characters included. The value looks correct in
        // the error, so the error has to say why it isn't.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationPreflight.Validate(Configuration(("Auth:Oidc:Enabled", "\"true\""))));

        Assert.Contains("quote characters are part of the value", ex.Message);
        Assert.Contains("env-file", ex.Message);
    }

    [Fact]
    public void QuoteHintStaysOutOfUnrelatedFailures()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationPreflight.Validate(Configuration(("Auth:Oidc:Enabled", "1"))));

        Assert.DoesNotContain("quote characters", ex.Message);
    }

    [Fact]
    public void ChecksNumericSettingsToo()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationPreflight.Validate(Configuration(("Worker:ScheduleIntervalSeconds", "10 minutes"))));

        Assert.Contains("Worker__ScheduleIntervalSeconds", ex.Message);
        Assert.Contains("a whole number", ex.Message);
    }

    [Fact]
    public void ChecksSettingsWithNoOptionsClass()
    {
        // Database:MigrateOnStartup is read straight off IConfiguration, so no
        // amount of reflection over options classes would reach it.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationPreflight.Validate(Configuration(("Database:MigrateOnStartup", "1"))));

        Assert.Contains("Database__MigrateOnStartup", ex.Message);
    }

    [Fact]
    public void ListValuesBindElementByElementAndAreLeftAlone()
    {
        // Network__TrustedNetworks__0 is a string element, not a conversion.
        ConfigurationPreflight.Validate(Configuration(
            ("Network:UseForwardedHeaders", "true"),
            ("Network:TrustedNetworks:0", "172.16.0.0/12")));
    }

    [Fact]
    public void PointsAtTheDocument()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationPreflight.Validate(Configuration(("Alerts:Enabled", "1"))));

        Assert.Contains("docs/ops/configuration.md", ex.Message);
    }

    [Theory]
    [MemberData(nameof(EveryBoundSection))]
    public void EveryBoundSectionIsReachedByTheWalk(string section, Type type)
    {
        // A section listed but never actually bound here would pass silently.
        // Feed each one a value no type converts and require a complaint that
        // names that section's own variable.
        var property = type.GetProperties()
            .First(p => p.GetSetMethod() is not null
                && p.PropertyType != typeof(string)
                && !p.PropertyType.IsArray);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationPreflight.Validate(Configuration(($"{section}:{property.Name}", "not-a-value"))));

        Assert.Contains(
            ConfigurationPreflight.EnvironmentVariableName($"{section}:{property.Name}"),
            ex.Message);
    }

    public static TheoryData<string, Type> EveryBoundSection()
    {
        var data = new TheoryData<string, Type>();

        foreach (var (section, type) in ConfigurationPreflight.BoundSections)
        {
            data.Add(section, type);
        }

        return data;
    }
}
