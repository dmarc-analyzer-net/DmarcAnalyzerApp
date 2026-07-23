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
        using var parserStream = NormalizeReportXml(sourceBuffer, normalizationMessages);

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

        var records = feedback.Record?
            .Select(record =>
            {
                var dkimAuth = record.AuthResults?.Dkim?
                    .Select(x => new DmarcReportRecordDkimAuthParseResult(
                        x.Domain ?? string.Empty,
                        x.Selector ?? string.Empty,
                        x.Result.ToString().ToLowerInvariant(),
                        x.HumanResult ?? string.Empty))
                    .ToArray()
                    ?? Array.Empty<DmarcReportRecordDkimAuthParseResult>();

                var spfAuth = record.AuthResults?.Spf?
                    .Select(x => new DmarcReportRecordSpfAuthParseResult(
                        x.Domain ?? string.Empty,
                        x.Scope?.ToString().ToLowerInvariant() ?? string.Empty,
                        x.Result.ToString().ToLowerInvariant(),
                        x.HumanResult ?? string.Empty))
                    .ToArray()
                    ?? Array.Empty<DmarcReportRecordSpfAuthParseResult>();

                return new DmarcReportRecordParseResult(
                    record.Row?.SourceIp ?? string.Empty,
                    record.Row?.Count ?? 0,
                    record.Row?.PolicyEvaluated?.Disposition.ToString().ToLowerInvariant() ?? string.Empty,
                    record.Row?.PolicyEvaluated?.Dkim.ToString().ToLowerInvariant() ?? string.Empty,
                    record.Row?.PolicyEvaluated?.Spf.ToString().ToLowerInvariant() ?? string.Empty,
                    record.Identifiers?.HeaderFrom ?? string.Empty,
                    record.Identifiers?.EnvelopeFrom ?? string.Empty,
                    record.Identifiers?.EnvelopeTo ?? string.Empty,
                    dkimAuth,
                    spfAuth);
            })
            .ToArray()
            ?? Array.Empty<DmarcReportRecordParseResult>();

        return new DmarcReportParseResult(
            metadata.OrgName ?? string.Empty,
            metadata.ReportId ?? string.Empty,
            DateTimeOffset.FromUnixTimeSeconds(dateRange.Begin).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(dateRange.End).UtcDateTime,
            policyPublished.Domain ?? string.Empty,
            feedback.Record?.Length ?? 0,
            records,
            aggregateReport.HasWarnings || normalizationMessages.Count > 0,
            aggregateReport.HasErrors,
            validationMessages,
            MapDisposition(policyPublished.P),
            MapDisposition(policyPublished.Sp),
            ParsePercent(policyPublished.Percent),
            MapAlignment(policyPublished.Adkim),
            MapAlignment(policyPublished.Aspf));
    }

    private static string MapDisposition(DispositionType disposition) => disposition switch
    {
        DispositionType.Reject => "reject",
        DispositionType.Quarantine => "quarantine",
        _ => "none",
    };

    private static string MapAlignment(AlignmentType? alignment) => alignment switch
    {
        AlignmentType.Strict => "strict",
        _ => "relaxed",
    };

    private static int ParsePercent(string? percent)
        => int.TryParse(percent, out var value) && value is >= 0 and <= 100 ? value : 100;

    private static MemoryStream CopyToMemory(Stream xmlStream)
    {
        var copy = new MemoryStream();
        xmlStream.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    private static MemoryStream NormalizeReportXml(Stream xmlStream, List<string> normalizationMessages)
    {
        try
        {
            xmlStream.Position = 0;
            var document = XDocument.Load(xmlStream);
            var updated = false;
            var scopeNormalized = false;

            // DMARCbis reports namespace the schema (urn:ietf:params:xml:ns:dmarc-2.0),
            // which the DmarcRua serializer does not expect. The aggregate format is
            // field-compatible for everything we read, so strip namespaces entirely.
            if (document.Root is not null && document.Root.Name.Namespace != XNamespace.None)
            {
                var reportNamespace = document.Root.Name.NamespaceName;
                foreach (var element in document.Descendants())
                {
                    element.Name = XNamespace.None + element.Name.LocalName;
                    element.Attributes()
                        .Where(x => x.IsNamespaceDeclaration)
                        .Remove();
                }

                normalizationMessages.Add($"warning: stripped XML namespace '{reportNamespace}' for compatibility");
                updated = true;
            }

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
                    if (!scopeNormalized)
                    {
                        normalizationMessages.Add("warning: normalized SPF scope value 'helo' to 'mfrom' for compatibility");
                        scopeNormalized = true;
                    }
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
