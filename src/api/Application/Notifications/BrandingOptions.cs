using System.Text.RegularExpressions;

namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>
/// The agency's identity (`Branding:*`) on outbound mail and in the console. One brand
/// for the whole instance: the digest and a client's console login are the agency
/// speaking to its clients, so they carry the agency's name rather than the product's.
/// </summary>
public sealed partial class BrandingOptions
{
    /// <summary>Shown as the sender in the mail header and footer, and in the console header and tab title.</summary>
    public string Name { get; set; } = DefaultName;

    /// <summary>
    /// Absolute http(s) URL of a logo image, shown at most 40px high. Optional; many
    /// mail clients block images until the reader allows them, so the name is always
    /// shown as text as well.
    /// </summary>
    public string LogoUrl { get; set; } = string.Empty;

    /// <summary>Accent colour as a six-digit hex value, e.g. <c>#0c7568</c>. Anything else falls back to the default.</summary>
    public string AccentColor { get; set; } = DefaultAccentColor;

    /// <summary>
    /// Hides the "powered by DMARC Analyzer" line the console shows once the brand is
    /// customised. Has no effect on an unbranded instance, which never shows it.
    /// </summary>
    public bool HidePoweredBy { get; set; }

    public const string DefaultName = "DMARC Analyzer";

    public const string DefaultAccentColor = "#0c7568";

    /// <summary>The name to show: the configured one, trimmed, or the product's when blank.</summary>
    public string ResolvedName => string.IsNullOrWhiteSpace(Name) ? DefaultName : Name.Trim();

    /// <summary>The configured accent if it is a six-digit hex value, else the default.</summary>
    public string ResolvedAccentColor => HexColor().IsMatch(AccentColor ?? string.Empty)
        ? AccentColor!.ToLowerInvariant()
        : DefaultAccentColor;

    /// <summary>The logo URL if it is an absolute http(s) URL, else null.</summary>
    public string? ResolvedLogoUrl => Uri.TryCreate(LogoUrl, UriKind.Absolute, out var logo) &&
                                      (logo.Scheme == Uri.UriSchemeHttps || logo.Scheme == Uri.UriSchemeHttp)
        ? logo.AbsoluteUri
        : null;

    /// <summary>True once any of name, logo or accent differs from the product's own.</summary>
    public bool IsCustomized => ResolvedName != DefaultName
                                || ResolvedLogoUrl is not null
                                || ResolvedAccentColor != DefaultAccentColor;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
