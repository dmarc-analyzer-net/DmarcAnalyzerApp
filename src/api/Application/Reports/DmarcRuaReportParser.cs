using DmarcRua;
using System.Xml.Schema;
using System.Xml.Linq;

namespace DmarcAnalyzer.Api.Application.Reports;

public sealed class DmarcRuaReportParser : IDmarcReportParser
{
    public DmarcReportParseResult Parse(Stream xmlStream)
    {
        ArgumentNullException.ThrowIfNull(xmlStream);

        if (!xmlStream.CanRead)
        {
            throw new ArgumentException("stream must be readable", nameof(xmlStream));
        }

        using var sourceBuffer = CopyToMemory(xmlStream);
        var normalizationMessages = new List<string>();
        using var parserStream = NormalizeUnsupportedSpfScopes(sourceBuffer, normalizationMessages);

        var aggregateReport = new AggregateReport(parserStream);
        var feedback = aggregateReport.Feedback
            ?? throw new InvalidOperationException("DMARC report could not be deserialized.");

        var metadata = feedback.ReportMetadata
            ?? throw new InvalidOperationException("DMARC report is missing report_metadata.");
        var dateRange = metadata.DateRange
            ?? throw new InvalidOperationException("DMARC report is missing date_range.");
        var policyPublished = feedback.PolicyPublished
            ?? throw new InvalidOperationException("DMARC report is missing policy_published.");

        var validationMessages = aggregateReport.ValidationEvents
            .Select(x =>
            {
                var severity = x.Severity == XmlSeverityType.Error ? "error" : "warning";
                return $"{severity}: {x.Message}";
            })
            .Concat(normalizationMessages)
            .ToArray();

        return new DmarcReportParseResult(
            metadata.OrgName ?? string.Empty,
            metadata.ReportId ?? string.Empty,
            DateTimeOffset.FromUnixTimeSeconds(dateRange.Begin).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(dateRange.End).UtcDateTime,
            policyPublished.Domain ?? string.Empty,
            feedback.Record?.Length ?? 0,
            aggregateReport.HasWarnings || normalizationMessages.Count > 0,
            aggregateReport.HasErrors,
            validationMessages);
    }

    private static MemoryStream CopyToMemory(Stream xmlStream)
    {
        var copy = new MemoryStream();
        xmlStream.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    private static MemoryStream NormalizeUnsupportedSpfScopes(Stream xmlStream, List<string> normalizationMessages)
    {
        try
        {
            xmlStream.Position = 0;
            var document = XDocument.Load(xmlStream);
            var updated = false;

            foreach (var scopeElement in document.Descendants().Where(x => x.Name.LocalName == "scope"))
            {
                var value = (scopeElement.Value ?? string.Empty).Trim().ToLowerInvariant();
                if (value == "mfrom")
                {
                    continue;
                }

                if (value == "helo")
                {
                    scopeElement.Value = "mfrom";
                    updated = true;
                }
            }

            if (!updated)
            {
                xmlStream.Position = 0;
                var original = new MemoryStream();
                xmlStream.CopyTo(original);
                original.Position = 0;
                return original;
            }

            normalizationMessages.Add("warning: normalized SPF scope value 'helo' to 'mfrom' for compatibility");
            var normalized = new MemoryStream();
            document.Save(normalized);
            normalized.Position = 0;
            return normalized;
        }
        catch
        {
            xmlStream.Position = 0;
            var original = new MemoryStream();
            xmlStream.CopyTo(original);
            original.Position = 0;
            return original;
        }
    }
}
