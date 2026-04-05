namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class DmarcReportRecordDkimAuthResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DmarcReportRecordId { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string Selector { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public string HumanResult { get; set; } = string.Empty;

    public DmarcReportRecord? DmarcReportRecord { get; set; }
}
