namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>SMTP relay settings (`Email:*`). Delivery is off until a host is set.</summary>
public sealed class EmailOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;

    /// <summary>STARTTLS on the configured port. Set false only for a local relay.</summary>
    public bool UseStartTls { get; set; } = true;

    /// <summary>Leave blank for an unauthenticated relay (common for in-cluster MTAs).</summary>
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "DMARC Analyzer";

    /// <summary>Optional. Empty means no Reply-To header — replies go to FromAddress.</summary>
    public string ReplyToAddress { get; set; } = string.Empty;

    /// <summary>Absolute base URL used to build links in emails, e.g. https://dmarc.example.com.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}
