namespace DmarcAnalyzer.Api.Application.Reports;

/// <summary>Parses DMARC aggregate (RUA) XML — see <see cref="DmarcRuaReportParser"/>.</summary>
public interface IDmarcReportParser
{
    /// <summary>
    /// Parses one report document. Malformed values are repaired and noted in
    /// the result's validation messages where possible; only a structurally
    /// unusable document throws.
    /// </summary>
    DmarcReportParseResult Parse(Stream xmlStream);
}
