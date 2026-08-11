namespace DmarcAnalyzer.Api.Contracts.ReportSources;

public sealed class CreateReportSourceRequest
{
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = "imap";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool UseTls { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public Guid DefaultClientId { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Delete report mail from this mailbox once it is older than the retention window.
    /// Defaults to false: a new source must not start deleting a customer's mail because
    /// somebody left a field out of the request.
    /// </summary>
    public bool DeleteAfterRetention { get; set; }
}
