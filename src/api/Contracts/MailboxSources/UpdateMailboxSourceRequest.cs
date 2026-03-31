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
}
