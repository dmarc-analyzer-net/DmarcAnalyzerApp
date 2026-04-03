namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class DmarcReportIngest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Guid MailboxSourceId { get; set; }
    public string PolicyDomain { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public DateTime ReportRangeBeginUtc { get; set; }
    public DateTime ReportRangeEndUtc { get; set; }
    public string OrganizationName { get; set; } = string.Empty;
    public int RecordCount { get; set; }
    public DateTime IngestedAtUtc { get; set; } = DateTime.UtcNow;

    public Client? Client { get; set; }
    public MailboxSource? MailboxSource { get; set; }
}
