namespace DmarcAnalyzer.Api.Application.Reports;

/// <summary>Parses DMARC aggregate (RUA) XML — see <see cref="DmarcRuaReportParser"/>.</summary>
public interface IDmarcReportParser
{
    /// <summary>
    /// Parses one report document. Malformed values are repaired and noted in
    /// the result's validation messages where possible; a structurally unusable
    /// document throws, as does a null or unreadable stream
    /// (<see cref="ArgumentNullException"/>/<see cref="ArgumentException"/>).
    /// </summary>
    DmarcReportParseResult Parse(Stream xmlStream);
}
