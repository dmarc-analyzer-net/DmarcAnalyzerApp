namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>
/// The agency's identity on outbound mail (`Branding:*`). One brand for the whole
/// instance: the digest is the agency speaking to its clients, so it carries the
/// agency's name rather than the product's.
/// </summary>
public sealed class BrandingOptions
{
    /// <summary>Shown as the sender in the mail header and footer.</summary>
    public string Name { get; set; } = "DMARC Analyzer";

    /// <summary>
    /// Absolute http(s) URL of a logo image, shown at most 40px high. Optional; many
    /// mail clients block images until the reader allows them, so the name is always
    /// shown as text as well.
    /// </summary>
    public string LogoUrl { get; set; } = string.Empty;

    /// <summary>Accent colour as a six-digit hex value, e.g. <c>#0c7568</c>. Anything else falls back to the default.</summary>
    public string AccentColor { get; set; } = DefaultAccentColor;

    public const string DefaultAccentColor = "#0c7568";
}
