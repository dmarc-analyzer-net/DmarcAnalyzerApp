namespace DmarcAnalyzer.Api.Application.Reports;

/// <summary>
/// The aggregate report formats the parser recognises, keyed by the root namespace.
/// Stored in <c>dmarc_report.ReportFormat</c>; a null there means the row predates the
/// column, so the report-level fields that came with it are "not recorded" rather than
/// "not sent".
/// </summary>
public static class DmarcReportFormats
{
    /// <summary>RFC 7489 Appendix C: no root namespace.</summary>
    public const string Rfc7489 = "rfc7489";

    /// <summary>RFC 9990 Appendix A: <c>urn:ietf:params:xml:ns:dmarc-2.0</c>.</summary>
    public const string Rfc9990 = "rfc9990";
}
