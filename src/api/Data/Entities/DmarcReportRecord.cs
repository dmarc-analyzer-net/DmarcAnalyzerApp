namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class DmarcReportRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DmarcReportId { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public int MessageCount { get; set; }
    public string Disposition { get; set; } = string.Empty;
    public string DkimResult { get; set; } = string.Empty;
    public string SpfResult { get; set; } = string.Empty;
    public string HeaderFrom { get; set; } = string.Empty;
    public string EnvelopeFrom { get; set; } = string.Empty;
    public string EnvelopeTo { get; set; } = string.Empty;

    public DmarcReport? DmarcReport { get; set; }
    public ICollection<DmarcReportRecordDkimAuthResult> DkimAuthResults { get; set; } = new List<DmarcReportRecordDkimAuthResult>();
    public ICollection<DmarcReportRecordSpfAuthResult> SpfAuthResults { get; set; } = new List<DmarcReportRecordSpfAuthResult>();
}
