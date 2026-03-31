namespace DmarcAnalyzer.Api.Contracts.MailboxSources;

public sealed class CreateMailboxSourceRequest
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
}
