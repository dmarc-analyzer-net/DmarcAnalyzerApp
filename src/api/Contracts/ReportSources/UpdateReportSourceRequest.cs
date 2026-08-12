namespace DmarcAnalyzer.Api.Contracts.ReportSources;

public sealed class UpdateReportSourceRequest
{
    public string? Name { get; set; }
    public string? Protocol { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public bool? UseTls { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public Guid? DefaultClientId { get; set; }
    public bool? IsActive { get; set; }

    /// <summary>
    /// Delete report mail from this mailbox once it is older than the retention window.
    /// Irreversible and off by default — see <c>ReportSource.DeleteAfterRetention</c>.
    /// </summary>
    public bool? DeleteAfterRetention { get; set; }

    /// <summary>
    /// Whether this source may ingest reports for domains another client owns. Defaults
    /// to true, which is how every source behaved before the switch existed.
    /// </summary>
    public bool? AllowForeignDomains { get; set; }
}
