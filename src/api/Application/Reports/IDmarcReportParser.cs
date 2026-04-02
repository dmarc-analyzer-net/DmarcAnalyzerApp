namespace DmarcAnalyzer.Api.Application.Reports;

public interface IDmarcReportParser
{
    DmarcReportParseResult Parse(Stream xmlStream);
}
