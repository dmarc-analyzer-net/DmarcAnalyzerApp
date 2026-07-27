namespace DmarcAnalyzer.Api.Contracts.MailboxSources;

public sealed class UpdateMailboxSourceRequest
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
    /// Irreversible and off by default — see <c>MailboxSource.DeleteAfterRetention</c>.
    /// </summary>
    public bool? DeleteAfterRetention { get; set; }
}
