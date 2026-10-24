using DmarcAnalyzer.Api.Application.Notifications;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class BrandingOptionsTests
{
    [Fact]
    public void Defaults_AreNotCustomized()
    {
        var options = new BrandingOptions();

        Assert.Equal(BrandingOptions.DefaultName, options.ResolvedName);
        Assert.Equal(BrandingOptions.DefaultAccentColor, options.ResolvedAccentColor);
        Assert.Null(options.ResolvedLogoUrl);
        Assert.False(options.IsCustomized);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("#0c7568;background:url(x)")]
    [InlineData("")]
    public void AccentColor_FallsBackUnlessSixDigitHex(string value)
    {
        var options = new BrandingOptions { AccentColor = value };

        Assert.Equal(BrandingOptions.DefaultAccentColor, options.ResolvedAccentColor);
        Assert.False(options.IsCustomized);
    }

    [Fact]
    public void AccentColor_IsLowercased_SoTheDefaultInCapitalsIsNotACustomization()
    {
        var options = new BrandingOptions { AccentColor = "#0C7568" };

        Assert.Equal("#0c7568", options.ResolvedAccentColor);
        Assert.False(options.IsCustomized);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/svg+xml,<svg/>")]
    [InlineData("/logo.png")]
    [InlineData("not a url")]
    public void LogoUrl_IgnoredUnlessAbsoluteHttp(string value)
    {
        var options = new BrandingOptions { LogoUrl = value };

        Assert.Null(options.ResolvedLogoUrl);
        Assert.False(options.IsCustomized);
    }

    [Fact]
    public void Name_IsTrimmed_AndBlankMeansTheDefault()
    {
        Assert.Equal("Example Agency", new BrandingOptions { Name = "  Example Agency " }.ResolvedName);
        Assert.Equal(BrandingOptions.DefaultName, new BrandingOptions { Name = "   " }.ResolvedName);
    }

    [Theory]
    [InlineData("Example Agency", "", "#0c7568")]
    [InlineData("DMARC Analyzer", "https://example.com/logo.png", "#0c7568")]
    [InlineData("DMARC Analyzer", "", "#f5c400")]
    public void AnyOneSetting_IsACustomization(string name, string logoUrl, string accent)
    {
        var options = new BrandingOptions { Name = name, LogoUrl = logoUrl, AccentColor = accent };

        Assert.True(options.IsCustomized);
    }
}
