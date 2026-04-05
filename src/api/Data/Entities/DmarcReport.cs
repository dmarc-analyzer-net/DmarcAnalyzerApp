namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class DmarcReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }
    public Guid MailboxSourceId { get; set; }
    public string OrganizationName { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public DateTime RangeBeginUtc { get; set; }
    public DateTime RangeEndUtc { get; set; }
    public int RecordCount { get; set; }
    public DateTime IngestedAtUtc { get; set; } = DateTime.UtcNow;

    public Domain? Domain { get; set; }
    public MailboxSource? MailboxSource { get; set; }
    public ICollection<DmarcReportRecord> Records { get; set; } = new List<DmarcReportRecord>();
}
