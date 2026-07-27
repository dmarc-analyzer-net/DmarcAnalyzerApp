using DmarcRua;
using System.Xml;
using System.Xml.Schema;
using System.Text;
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
        var hasSubdomainPolicy = HasSubdomainPolicyTag(sourceBuffer);
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
            hasSubdomainPolicy ? MapDisposition(policyPublished.Sp) : null,
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

    /// <summary>
    /// Whether the reporter actually sent an sp tag.
    /// <para>
    /// adkim, aspf and pct all have fixed defaults, so collapsing an absent tag to
    /// its default is correct for them. sp is the exception: RFC 7489 §6.3 defines
    /// its default as "whatever p is", which is derived rather than fixed. The XSD
    /// nonetheless defaults sp to "none", and DmarcRua 2.0.0 exposes no *Specified
    /// members, so a deserialized absent sp is indistinguishable from an explicit
    /// sp=none — which reads as a policy regression on a p=reject domain.
    /// </para>
    /// <para>
    /// Presence therefore has to come from the XML. policy_published precedes the
    /// record list, so this stops as soon as that element closes and never walks
    /// the (potentially very large) body of the report.
    /// </para>
    /// </summary>
    private static bool HasSubdomainPolicyTag(Stream xmlStream)
    {
        try
        {
            xmlStream.Position = 0;
            using var reader = XmlReader.Create(xmlStream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
                IgnoreComments = true,
                CloseInput = false,
            });

            var inPolicyPublished = false;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "policy_published")
                    {
                        inPolicyPublished = true;
                    }
                    else if (inPolicyPublished && reader.LocalName == "sp")
                    {
                        return true;
                    }
                    else if (reader.LocalName == "record")
                    {
                        break;
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "policy_published")
                {
                    break;
                }
            }

            return false;
        }
        catch (XmlException)
        {
            // Malformed XML: report it as absent and let the DmarcRua pass surface
            // the parse error, rather than failing ingestion twice for one cause.
            return false;
        }
        finally
        {
            xmlStream.Position = 0;
        }
    }

    private static int ParsePercent(string? percent)
        => int.TryParse(percent, out var value) && value is >= 0 and <= 100 ? value : 100;

    private static MemoryStream CopyToMemory(Stream xmlStream)
    {
        var copy = new MemoryStream();
        xmlStream.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    /// <summary>
    /// Element values the DmarcRua enums accept, and what an unrecognised value becomes.
    /// Keyed by (parent, element) because auth_results reuses the names dkim and spf for
    /// complex types whose verdict lives in a nested result element.
    /// </summary>
    private static readonly (string Parent, string Element, string[] Allowed, string Fallback)[] EnumRepairs =
    [
        // DMARCResultType. No empty member, so an empty element here is fatal. 'fail' is the
        // conservative reading: compliance is dkim=pass OR spf=pass, so a record whose other
        // mechanism passed still counts, and a missing one never invents a pass.
        ("policy_evaluated", "dkim", ["pass", "fail"], "fail"),
        ("policy_evaluated", "spf", ["pass", "fail"], "fail"),

        // DispositionType. 'none' matches what MapDisposition already does with anything
        // that is not reject or quarantine.
        ("policy_evaluated", "disposition", ["none", "quarantine", "reject", "nil", ""], "none"),

        // DKIMResultType / SpfResultType, under auth_results. These are reported detail
        // rather than the DMARC verdict, so they do not move compliance; permerror reads as
        // "could not be evaluated", which is what an unparseable value amounts to.
        ("dkim", "result",
            ["none", "neutral", "pass", "fail", "policy", "softfail", "temperror", "permerror", "hardfail", ""],
            "permerror"),
        ("spf", "result",
            ["none", "neutral", "pass", "fail", "softfail", "temperror", "permerror", "hardfail", ""],
            "permerror"),
    ];

    /// <summary>
    /// RFC 4408 result names that RFC 7208 renamed. A reporter still using the old name is
    /// stating a result we understand exactly, so these are translated rather than defaulted.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyResultNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["unknown"] = "permerror",
            ["error"] = "temperror",
        };

    /// <summary>
    /// Recovers a report whose XML was cut short, but only when nothing was lost with it.
    /// <para>
    /// One reporter sends aggregate XML ending at "&lt;/feedback" — the final '&gt;' never
    /// arrives. Every record is present and closed; one character of markup is missing. It
    /// is not our truncation: all three extraction paths copy the payload whole, so the byte
    /// is gone before we see it. Left alone, 100% of that reporter's reports are discarded.
    /// </para>
    /// <para>
    /// The guard is that the cut must fall outside any record. If a record is still open at
    /// the point the reader failed, the report was genuinely cut mid-record, and completing
    /// it would ingest a partial report as though it were whole — under-counting that period,
    /// and permanently, since the unique index would treat a later complete copy as a
    /// duplicate. In that case this returns null and the report fails as before.
    /// </para>
    /// </summary>
    private static XDocument? TryCloseTruncatedDocument(Stream xmlStream, List<string> normalizationMessages)
    {
        try
        {
            xmlStream.Position = 0;
            using var reader = new StreamReader(xmlStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var text = reader.ReadToEnd();

            // A truncation usually lands mid-tag, so discard the dangling fragment first;
            // otherwise the open-element scan below reads a tag that was never finished.
            var lastClosed = text.LastIndexOf('>');
            if (lastClosed < 0)
            {
                return null;
            }

            var trimmed = text[..(lastClosed + 1)];
            var open = OpenElementsOf(trimmed);
            if (open is null || open.Count == 0)
            {
                return null;
            }

            // The guard. "record" still open means a record was cut in half.
            if (open.Any(x => string.Equals(x, "record", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var closing = string.Concat(open.Select(x => $"</{x}>"));
            var document = XDocument.Parse(trimmed + closing);

            normalizationMessages.Add(
                $"warning: completed a truncated document by closing {closing} for compatibility");

            return document;
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// The elements still open at the end of a truncated document, innermost first so the
    /// caller can concatenate closing tags directly. Null if nothing could be read. Uses a
    /// real reader rather than tag matching, so comments, CDATA and self-closing elements
    /// are not miscounted.
    /// </summary>
    private static List<string>? OpenElementsOf(string xmlFragment)
    {
        var open = new List<string>();

        try
        {
            using var reader = XmlReader.Create(new StringReader(xmlFragment), new XmlReaderSettings
            {
                ConformanceLevel = ConformanceLevel.Document,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
                IgnoreComments = true,
            });

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when !reader.IsEmptyElement:
                        open.Add(reader.LocalName);
                        break;
                    case XmlNodeType.EndElement when open.Count > 0:
                        open.RemoveAt(open.Count - 1);
                        break;
                }
            }
        }
        catch (XmlException)
        {
            // Unexpected EOF is the expected outcome for a truncated fragment: whatever the
            // reader managed before that still tells us which elements were left open.
            if (open.Count == 0)
            {
                return null;
            }
        }

        open.Reverse();
        return open;
    }

    private static MemoryStream NormalizeReportXml(Stream xmlStream, List<string> normalizationMessages)
    {
        try
        {
            var updated = false;
            var scopeNormalized = false;

            xmlStream.Position = 0;
            XDocument document;
            try
            {
                document = XDocument.Load(xmlStream);
            }
            catch (XmlException)
            {
                // Every repair below needs a loadable document, and this catch used to be
                // the outer one — a document that would not load silently skipped all of
                // them and failed downstream with whatever DmarcRua complained about first,
                // which sent me looking in the wrong place entirely.
                var recovered = TryCloseTruncatedDocument(xmlStream, normalizationMessages);
                if (recovered is null)
                {
                    throw;
                }

                document = recovered;
                updated = true;
            }

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

            // Reporters send values these enums do not accept, and XmlSerializer treats that
            // as fatal: one bad token fails the whole <feedback> document and discards every
            // record in it, 28 on average. Observed in one mailbox pass alone: '' and <dkim/>
            // for policy_evaluated dkim (~1.5% of attachments), 'unknown' for an SPF auth
            // result, and '15' for a disposition. Repairing the value costs one field;
            // rejecting the document costs the whole report.
            //
            // The accepted sets below are the XmlEnum names on the DmarcRua enums themselves,
            // not the XSD, so they cannot drift from what the serializer will actually take.
            // Note DispositionType, SpfResultType and DKIMResultType all accept '' — only
            // DMARCResultType is strictly pass|fail, which is why an empty one was fatal.
            foreach (var element in document.Descendants())
            {
                var parent = element.Parent?.Name.LocalName;
                if (parent is null)
                {
                    continue;
                }

                var repair = EnumRepairs.FirstOrDefault(x =>
                    x.Parent == parent && x.Element == element.Name.LocalName);
                if (repair.Element is null)
                {
                    continue;
                }

                var original = (element.Value ?? string.Empty).Trim();
                var candidate = LegacyResultNames.TryGetValue(original, out var modern) ? modern : original;

                if (repair.Allowed.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    // Whitespace-only where '' is legal still needs trimming to become ''.
                    if (!string.Equals(element.Value, candidate, StringComparison.Ordinal))
                    {
                        element.Value = candidate;
                        updated = true;
                    }

                    continue;
                }

                element.Value = repair.Fallback;
                updated = true;

                // Named, and deduplicated, so a large report cannot emit thousands of copies
                // while an operator still learns which value was actually substituted.
                var message =
                    $"warning: replaced unrecognised {parent}/{element.Name.LocalName} value " +
                    $"'{original}' with '{repair.Fallback}' for compatibility";
                if (!normalizationMessages.Contains(message))
                {
                    normalizationMessages.Add(message);
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
