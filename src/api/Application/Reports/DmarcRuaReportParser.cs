using DmarcRua;
using System.Xml;
using System.Xml.Schema;
using System.Text;
using System.Xml.Linq;

namespace DmarcAnalyzer.Api.Application.Reports;

/// <summary>
/// The RUA parser: DmarcRua for the classic RFC 7489 namespace, plus native
/// handling of the DMARCbis (dmarc-2.0) namespace and repair of the
/// out-of-vocabulary values real reporters send.
/// </summary>
public sealed class DmarcRuaReportParser : IDmarcReportParser
{
    private const string DmarcBisNamespace = "urn:ietf:params:xml:ns:dmarc-2.0";

    private static readonly string[] DmarcBisActionDispositions =
        ["none", "pass", "quarantine", "reject"];

    /// <inheritdoc />
    public DmarcReportParseResult Parse(Stream xmlStream)
    {
        ArgumentNullException.ThrowIfNull(xmlStream);

        if (!xmlStream.CanRead)
        {
            throw new ArgumentException("stream must be readable", nameof(xmlStream));
        }

        using var sourceBuffer = CopyToMemory(xmlStream);
        var normalizationMessages = new List<DmarcReportParseMessage>();
        var normalized = NormalizeReportXml(sourceBuffer, normalizationMessages);
        using var parserStream = normalized.Stream;

        // Sniffed on the document DmarcRua will read, not the original: an sp the repair
        // pass removed as unusable has to read as absent here too.
        var hasSubdomainPolicy = HasSubdomainPolicyTag(parserStream);

        var aggregateReport = new AggregateReport(parserStream);
        var feedback = aggregateReport.Feedback
            ?? throw new InvalidOperationException("DMARC report could not be deserialized.");

        var metadata = feedback.ReportMetadata
            ?? throw new InvalidOperationException("DMARC report is missing report_metadata.");
        var dateRange = metadata.DateRange
            ?? throw new InvalidOperationException("DMARC report is missing date_range.");
        var policyPublished = feedback.PolicyPublished
            ?? throw new InvalidOperationException("DMARC report is missing policy_published.");

        // The captured RFC 9990 dispositions are keyed by position among the document's own
        // <record> elements, so they are only safe to use while the deserializer produced
        // exactly that many records. Nothing observed makes the two disagree, but if they ever
        // did, an index would carry one record's disposition onto another — storing a value the
        // reporter never sent for that source, which is worse than falling back to the v1 enum.
        var actionDispositions = normalized.DmarcBisDispositions.Count == (feedback.Record?.Length ?? 0)
            ? normalized.DmarcBisDispositions
            : Array.Empty<string?>();

        var deserializedRecords = feedback.Record?
            .Select((record, index) =>
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
                    // Trimmed: a reporter that pretty-prints the element sends the IP with the
                    // surrounding newline and indentation inside it, and every lookup that
                    // matches on this column — source-detail, the DKIM/SPF auth joins — compares
                    // it against a trimmed query value, so the untrimmed form is a source no
                    // caller can ever address.
                    (record.Row?.SourceIp ?? string.Empty).Trim(),
                    record.Row?.Count ?? 0,
                    actionDispositions.ElementAtOrDefault(index)
                        ?? record.Row?.PolicyEvaluated?.Disposition.ToString().ToLowerInvariant()
                        ?? string.Empty,
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

        var records = deserializedRecords.Where(x => !IsEmptyRecord(x)).ToArray();
        var emptyRecords = deserializedRecords.Length - records.Length;
        if (emptyRecords > 0)
        {
            normalizationMessages.Add(DmarcReportParseMessage.Warning(
                $"dropped {emptyRecords} record(s) reporting no source IP and no messages"));
        }

        // Before the messages are collected: an unusable pct adds one.
        var publishedPct = ParsePercent(policyPublished.Percent, normalizationMessages);

        // Deduplicated as a whole, not only per repair: the schema validator repeats itself
        // (one 'lang' error per attribute it finds), and the stored list is read by people.
        var validationMessages = aggregateReport.ValidationEvents
            .Select(x => x.Severity == XmlSeverityType.Error
                ? DmarcReportParseMessage.Error(x.Message)
                : DmarcReportParseMessage.Warning(x.Message))
            .Concat(normalizationMessages)
            .Distinct()
            .ToArray();

        // Read from the root namespace before the normalization pass stripped it. It is
        // null only when that pass could not load the document, and DmarcRua reads with the
        // same XML stack, so deserialization has failed before this line in practice.
        var reportFormat = normalized.ReportFormat
            ?? throw new InvalidOperationException("DMARC report format could not be determined.");

        // error, np, testing and discovery_method are read whatever the format. np, testing
        // and discovery_method are RFC 9990's, but real RFC 7489 reports carry np (one large
        // reporter sends it in no namespace), and the library models all of them without
        // caring about the namespace. Unlike sp, the three are genuinely nullable in
        // DmarcRua, so absence needs no XML sniff. Errors are kept as sent, minus the
        // whitespace a pretty-printed element wraps around its text.
        var reportErrors = (metadata.Error ?? [])
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToArray();

        return new DmarcReportParseResult(
            metadata.OrgName ?? string.Empty,
            metadata.ReportId ?? string.Empty,
            DateTimeOffset.FromUnixTimeSeconds(dateRange.Begin).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(dateRange.End).UtcDateTime,
            policyPublished.Domain ?? string.Empty,
            feedback.Record?.Length ?? 0,
            records,
            // Warnings are the warning-severity messages, not "anything was said": an
            // info message is how an RFC 9990 report is read, and must not flag it.
            validationMessages.Any(x => x.Severity == DmarcReportParseSeverity.Warning),
            aggregateReport.HasErrors,
            validationMessages,
            MapDisposition(policyPublished.P),
            hasSubdomainPolicy ? MapDisposition(policyPublished.Sp) : null,
            publishedPct,
            MapAlignment(policyPublished.Adkim),
            MapAlignment(policyPublished.Aspf),
            reportFormat,
            MapOptionalDisposition(policyPublished.Np),
            policyPublished.Testing is { } testing ? testing == TestingType.Yes : null,
            policyPublished.DiscoveryMethod is { } discovery
                ? (discovery == DiscoveryType.Treewalk ? "treewalk" : "psl")
                : null,
            normalized.Generator,
            reportErrors);
    }

    /// <summary>
    /// A record that reports no sender and no mail: no source IP, and a count of zero.
    /// <para>
    /// Observed in #190 from wp.pl and o2.pl — the same operator, which is why the blank row
    /// there claimed two reporters. Both send a whole report whose single record is empty
    /// throughout: <c>&lt;source_ip&gt;&lt;/source_ip&gt;&lt;count&gt;0&lt;/count&gt;</c>,
    /// empty policy_evaluated, empty identifiers, empty auth_results. It is not corruption
    /// and nothing failed to parse — it is a "nothing to report" heartbeat for the window,
    /// and this is where it stops. The same end state arrives when the deserializer cannot
    /// fill a <c>&lt;row&gt;</c> at all (absent, miscased, or namespaced on its own), because
    /// every field the row carries then falls to its default; both are dropped here.
    /// </para>
    /// <para>
    /// Stored, such a record becomes a sending source of its own: the analytics aggregation
    /// groups by SourceIp, so an empty one appears in the table as a blank row with zeroes
    /// across it, inflates the domain's source count, and — since source-detail needs an IP
    /// to query by — answers 400 when an operator expands it, which is what #190 reported.
    /// </para>
    /// <para>
    /// The condition is deliberately both halves, not just the blank IP. A record with a
    /// blank IP and a real count is mail that genuinely arrived and was reported; dropping it
    /// would under-count the domain's volume and the compliance denominator, which is worse
    /// than showing it unattributed. Only a record that reports neither sender nor mail can
    /// be dropped without losing something.
    /// </para>
    /// <para>
    /// The report itself is still ingested — a reporter that saw nothing did still report,
    /// and the domain is still receiving reports from it. RecordCount keeps the reporter's own
    /// record total, so a report this fired on stays discoverable afterwards: it has more
    /// records than rows in dmarc_report_record.
    /// </para>
    /// </summary>
    private static bool IsEmptyRecord(DmarcReportRecordParseResult record)
        => record.SourceIp.Length == 0 && record.MessageCount <= 0;

    private static string MapDisposition(DispositionType disposition) => disposition switch
    {
        DispositionType.Reject => "reject",
        DispositionType.Quarantine => "quarantine",
        _ => "none",
    };

    /// <summary>
    /// adkim/aspf, defaulting to relaxed when the reporter does not usably state one.
    /// <para>
    /// Absent means "relaxed" here, and that is this method's decision rather than the
    /// library's: unlike sp, adkim and aspf have fixed RFC 7489 §6.3 defaults, so collapsing
    /// an absent tag to its default is correct and needs no HasSubdomainPolicyTag-style
    /// presence sniff. DmarcRua returns null for absent, empty and unrecognised alike, and
    /// all three land on relaxed — an unparseable alignment is not a reason to claim the
    /// stricter policy.
    /// </para>
    /// <para>
    /// This read the raw <c>AdkimRaw</c>/<c>AspfRaw</c> strings between 2.0.1 and 2.1.0,
    /// because 2.0.1's computed properties called <c>Regex.Replace</c> on a value that can be
    /// null and threw ArgumentNullException on merely *reading* an absent tag — after
    /// deserialization had already succeeded, so it surfaced here rather than as a parse
    /// error. 2.1.0 null-guards the helper (danielsen/DmarcRua#11), so the properties are
    /// safe again and carry the library's own trimming and case folding, which is why
    /// '&#160;S&#160;' still reads as strict.
    /// </para>
    /// </summary>
    private static string MapAlignment(AlignmentType? alignment)
        => alignment == AlignmentType.Strict ? "strict" : "relaxed";

    /// <summary>
    /// np, which unlike p has no value to fall back to: absent means "not sent", and an
    /// empty or nil element states no policy either, so neither is read as none.
    /// </summary>
    private static string? MapOptionalDisposition(DispositionType? disposition) => disposition switch
    {
        DispositionType.Reject => "reject",
        DispositionType.Quarantine => "quarantine",
        DispositionType.None => "none",
        _ => null,
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

    /// <summary>
    /// pct, or null when the reporter sent none.
    /// <para>
    /// This used to invent 100 for an absent tag. That is RFC 7489's default, but RFC 9990
    /// removed the tag, so every conformant RFC 9990 report was stored with a pct it never
    /// sent — the same problem sp had. Absent is stored as absent; what an absent pct means
    /// is the reader's decision, not the parser's. A value that is not a whole number from 0
    /// to 100 still becomes 100, with a warning, since there is no better reading of it.
    /// </para>
    /// </summary>
    private static int? ParsePercent(string? percent, List<DmarcReportParseMessage> normalizationMessages)
    {
        if (percent is null)
        {
            return null;
        }

        if (int.TryParse(percent, out var value) && value is >= 0 and <= 100)
        {
            return value;
        }

        normalizationMessages.Add(DmarcReportParseMessage.Warning(
            $"replaced unusable policy_published/pct value '{percent.Trim()}' with '100' for compatibility"));
        return 100;
    }

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
    /// complex types whose verdict lives in a nested result element. A null fallback
    /// removes the element instead: for an optional tag whose every substitute would be a
    /// claim, absent is the only honest reading.
    /// </summary>
    private static readonly (string Parent, string Element, string[] Allowed, string? Fallback)[] EnumRepairs =
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

        // SpfDomainScope. DmarcRua 2.0.0 modelled only mfrom, so helo — legal per RFC 7208
        // and sent by real reporters — was fatal, and this parser rewrote it to mfrom to save
        // the document. 2.0.1 added Helo, so the scope is now recorded as sent; the rewrite
        // was the last thing storing a value the reporter never reported. Kept as a repair
        // rather than dropped outright because the enum still has no empty member, so
        // <scope/> or a value neither of these is fatal, and mfrom is the safe reading —
        // it is the scope RFC 7489 aligns against and the one ~99% of reports send.
        ("spf", "scope", ["mfrom", "helo"], "mfrom"),

        // PolicyOverrideType, under policy_evaluated/reason. The allowed set is exactly what
        // DmarcRua 2.1 deserializes, not RFC 9990's list: that RFC added policy_test_mode,
        // and allowing a value the library does not model hands it to the serializer, which
        // throws and loses the whole report. Override reasons are not stored, so 'other'
        // loses nothing. policy_test_mode itself is rewritten silently in the loop below,
        // because it is a defined value rather than a reporter's mistake.
        ("reason", "type",
            ["forwarded", "sampled_out", "trusted_forwarder", "mailing_list", "local_policy", "other"],
            "other"),

        // policy_published. DispositionType, TestingType and DiscoveryType have no catch-all
        // member, so a value outside them threw in the serializer and lost the whole report
        // over a tag that describes the published record rather than any message in it. p
        // is required, and MapDisposition already reads anything that is not reject or
        // quarantine as none, so that is its fallback. The optional tags have no fallback
        // that is not a claim — sp=none on a p=reject domain reads as a regression — so an
        // unusable value is removed, and absent is then what it means: sp inherits p, the
        // others were not usably sent.
        ("policy_published", "p", ["none", "quarantine", "reject", "nil", ""], "none"),
        ("policy_published", "sp", ["none", "quarantine", "reject", "nil", ""], null),
        ("policy_published", "np", ["none", "quarantine", "reject", "nil", ""], null),
        ("policy_published", "testing", ["y", "n"], null),
        ("policy_published", "discovery_method", ["psl", "treewalk"], null),
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
    private static XDocument? TryCloseTruncatedDocument(Stream xmlStream, List<DmarcReportParseMessage> normalizationMessages)
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

            normalizationMessages.Add(DmarcReportParseMessage.Warning(
                $"completed a truncated document by closing {closing} for compatibility"));

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

    /// <summary>
    /// What <see cref="NormalizeReportXml"/> hands back: the document DmarcRua will read,
    /// plus everything that had to be read off the original before it was rewritten.
    /// </summary>
    /// <param name="ReportFormat">One of <see cref="DmarcReportFormats"/>; null when the document could not be loaded.</param>
    /// <param name="Generator">report_metadata/generator, which DmarcRua does not model; null when not sent.</param>
    private sealed record NormalizedReport(
        MemoryStream Stream,
        IReadOnlyList<string?> DmarcBisDispositions,
        string? ReportFormat,
        string? Generator);

    /// <summary>
    /// A root namespace that is neither RFC 7489's (none) nor RFC 9990's. Not a parse
    /// failure of the document, so it is its own type: the normalization pass swallows
    /// everything else and falls back to the original bytes, and this must not be swallowed.
    /// </summary>
    private sealed class UnsupportedReportNamespaceException(string namespaceName)
        : InvalidOperationException(
            $"DMARC report root namespace '{namespaceName}' is not a recognised aggregate report format.");

    /// <summary>The column is varchar(256); nothing longer is a generator name.</summary>
    private const int GeneratorMaxLength = 256;

    private static NormalizedReport NormalizeReportXml(
        Stream xmlStream,
        List<DmarcReportParseMessage> normalizationMessages)
    {
        try
        {
            var updated = false;

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

            // The format is the root namespace, and only the two defined ones are read.
            // This pass used to strip any namespace, which would have parsed a report in a
            // namespace nobody defined as RFC 7489 and stored it as such. A real-mailbox
            // sample of 12,759 reports found none and dmarc-2.0 only (no RFC 7489 report
            // carries a namespace: rua.xsd declares no targetNamespace), so there is no
            // allow-list to maintain — anything else is rejected, and the ingest path counts
            // it as a parse failure like any other document it cannot read.
            var reportNamespace = document.Root?.Name.NamespaceName ?? string.Empty;
            var reportFormat = reportNamespace switch
            {
                "" => DmarcReportFormats.Rfc7489,
                DmarcBisNamespace => DmarcReportFormats.Rfc9990,
                _ => throw new UnsupportedReportNamespaceException(reportNamespace),
            };

            var isDmarcBisReport = reportFormat == DmarcReportFormats.Rfc9990;
            var dmarcBisDispositions = isDmarcBisReport
                ? document.Root!
                    .Elements()
                    .Where(x => x.Name.LocalName == "record")
                    .Select(record =>
                    {
                        var reported = record
                            .Descendants()
                            .FirstOrDefault(x =>
                                x.Name.LocalName == "disposition"
                                && x.Parent?.Name.LocalName == "policy_evaluated")?
                            .Value
                            .Trim();

                        return DmarcBisActionDispositions.FirstOrDefault(x =>
                            string.Equals(x, reported, StringComparison.OrdinalIgnoreCase));
                    })
                    .ToArray()
                : Array.Empty<string?>();

            // Extensions go before the namespace strip, while the namespace still tells them
            // apart. RFC 9990 says a receiver ignores extensions it does not understand;
            // DmarcRua's schema knows none, so left in they turn a conformant report into
            // one with schema errors. The file-level extension is an <extension> wrapper in
            // the report's own namespace; the record-level ones are bare elements in a
            // foreign namespace after auth_results, with no wrapper. After the strip every
            // element is in no namespace, and the record-level ones would be
            // indistinguishable from an unknown report element. Removed without a message:
            // ignoring them is the specified behaviour, not a repair.
            XNamespace reportXmlNamespace = reportNamespace;
            var extensions = document.Root!
                .Descendants()
                .Where(x => x.Name == reportXmlNamespace + "extension" || x.Name.Namespace != reportXmlNamespace)
                .ToList();
            foreach (var extension in extensions)
            {
                // Already detached with a removed ancestor: nothing to do.
                if (extension.Parent is not null)
                {
                    extension.Remove();
                }
            }

            updated |= extensions.Count > 0;

            // generator is RFC 9990's and DmarcRua 2.1.1 does not model it: no property, and
            // its schema rejects the element. Read it here, where the document is already
            // loaded, then remove it so a conformant report validates.
            string? generator = null;
            var generatorElements = document.Root!
                .Elements(reportXmlNamespace + "report_metadata")
                .Elements(reportXmlNamespace + "generator")
                .ToList();
            if (generatorElements.Count > 0)
            {
                var value = generatorElements[0].Value.Trim();
                generator = value.Length switch
                {
                    0 => null,
                    > GeneratorMaxLength => value[..GeneratorMaxLength],
                    _ => value,
                };
                generatorElements.Remove();
                updated = true;
            }

            // lang is allowed on four elements in RFC 9990 and on none in DmarcRua's schema,
            // where it is an error. Language is not stored, so the attribute is dropped
            // rather than read.
            var langAttributes = document.Descendants()
                .Attributes()
                .Where(x => x.Name == "lang")
                .ToList();
            if (langAttributes.Count > 0)
            {
                langAttributes.Remove();
                updated = true;
            }

            // DMARCbis reports namespace the schema (urn:ietf:params:xml:ns:dmarc-2.0),
            // which the DmarcRua serializer does not expect. The aggregate format is
            // field-compatible for everything we read, so strip namespaces entirely. Only
            // RFC 9990 reports reach this block now: an unknown namespace was rejected above.
            //
            // Still needed on 2.0.1, despite its NamespaceIgnorantXmlReader: that reader only
            // hides namespaces from the *serializer*, while the validating reader beneath it
            // still sees them, and rua.xsd declares no targetNamespace. Measured on a
            // namespaced report with this pass removed: it deserializes, but the schema
            // matches nothing and every element raises "Could not find schema information" —
            // 31 warnings on a one-record report, and HasWarnings true. Stripping first keeps
            // validation meaningful and costs one explanatory message instead.
            if (document.Root is not null && document.Root.Name.Namespace != XNamespace.None)
            {
                foreach (var element in document.Descendants())
                {
                    element.Name = XNamespace.None + element.Name.LocalName;
                    element.Attributes()
                        .Where(x => x.IsNamespaceDeclaration)
                        .Remove();
                }

                // Info, not a warning: this is how every RFC 9990 report is read, and once
                // messages are shown a warning here would flag the whole format as faulty.
                normalizationMessages.Add(DmarcReportParseMessage.Info(
                    $"stripped XML namespace '{reportNamespace}' for compatibility"));
                updated = true;
            }

            // Reporters send values these enums do not accept, and XmlSerializer treats that
            // as fatal: one bad token fails the whole <feedback> document and discards every
            // record in it, 28 on average. Observed in one mailbox pass alone: '' and <dkim/>
            // for policy_evaluated dkim (~1.5% of attachments), 'unknown' for an SPF auth
            // result, and '15' for a disposition. Repairing the value costs one field;
            // rejecting the document costs the whole report.
            //
            // The accepted sets below started as the XmlEnum names on the DmarcRua enums
            // themselves, not the XSD. Note DispositionType, SpfResultType and DKIMResultType
            // all accept '' — only DMARCResultType was strictly pass|fail, which is why an
            // empty one was fatal.
            //
            // As of DmarcRua 2.0.1 those sets are deliberately *narrower* than the enums, so
            // this pass now pre-empts the library on three values rather than mirroring it:
            // DMARCResultType gained softfail and none, DKIMResultType gained invalid and
            // unknown, SpfResultType gained unknown. Keeping our reading matters most for
            // policy_evaluated none, which 2.0.1 aliases to Pass (None = Pass) — we call it
            // fail, because a mechanism the reporter did not evaluate must not manufacture a
            // DMARC pass and inflate compliance. softfail agrees either way; invalid and
            // unknown land on permerror here versus temperror upstream, and both read as
            // "could not be evaluated". Adding a value to a set below hands that value back
            // to the library, so check what its enum maps it to first.
            //
            // Snapshotted, because a repair with no fallback removes the element it is on,
            // and Descendants() cannot continue from a node that has just lost its parent.
            foreach (var element in document.Descendants().ToList())
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

                // RFC 9990 split the row's action disposition from the published-policy
                // DispositionType and added "pass". DmarcRua still models this field with
                // the v1 enum, so feed it a compatible transport value; the canonical v2
                // value captured above is restored when mapping each deserialized record.
                //
                // Upstream as danielsen/DmarcRua#12, with a patch verified against its own
                // corpus. Deserialization *throws* there rather than degrading the value, so
                // without this the whole document is lost, not just the field. Treat this as
                // the fix rather than a stopgap: 2.0.1 is 18 months old and unanswered.
                if (isDmarcBisReport
                    && parent == "policy_evaluated"
                    && element.Name.LocalName == "disposition"
                    && string.Equals(candidate, "pass", StringComparison.OrdinalIgnoreCase))
                {
                    element.Value = "none";
                    updated = true;
                    continue;
                }

                // RFC 9990 added the override reason policy_test_mode, which reporters send
                // when the domain publishes t=y. DmarcRua models only the RFC 7489 reasons,
                // so it travels as 'other'. Silent, like the disposition rewrite above: it is
                // a defined value rather than a reporter's mistake, and override reasons are
                // not stored, so nothing observable changes. A warning here would flag every
                // conformant report from a domain in test mode as repaired.
                if (parent == "reason"
                    && element.Name.LocalName == "type"
                    && string.Equals(candidate, "policy_test_mode", StringComparison.OrdinalIgnoreCase))
                {
                    element.Value = "other";
                    updated = true;
                    continue;
                }

                // Match case-insensitively but write back the *canonical* spelling, never the
                // reporter's. XmlSerializer matches XmlEnum names case-sensitively, so passing
                // 'PASS' or 'HELO' through unchanged after accepting it here would hand the
                // serializer a value it rejects and lose the whole document — the exact failure
                // this pass exists to prevent. Case-only differences are not a substitution of
                // meaning, so they are corrected silently and raise no warning.
                var canonical = repair.Allowed.FirstOrDefault(
                    x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase));

                if (canonical is not null)
                {
                    // Whitespace-only where '' is legal still needs trimming to become ''.
                    if (!string.Equals(element.Value, canonical, StringComparison.Ordinal))
                    {
                        element.Value = canonical;
                        updated = true;
                    }

                    continue;
                }

                DmarcReportParseMessage message;
                if (repair.Fallback is null)
                {
                    element.Remove();
                    message = DmarcReportParseMessage.Warning(
                        $"removed unusable {parent}/{element.Name.LocalName} value " +
                        $"'{original}' for compatibility");
                }
                else
                {
                    element.Value = repair.Fallback;
                    message = DmarcReportParseMessage.Warning(
                        $"replaced unrecognised {parent}/{element.Name.LocalName} value " +
                        $"'{original}' with '{repair.Fallback}' for compatibility");
                }

                updated = true;

                // Named, and deduplicated, so a large report cannot emit thousands of copies
                // while an operator still learns which value was actually substituted.
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
                return new NormalizedReport(original, dmarcBisDispositions, reportFormat, generator);
            }

            var normalized = new MemoryStream();
            document.Save(normalized);
            normalized.Position = 0;
            return new NormalizedReport(normalized, dmarcBisDispositions, reportFormat, generator);
        }
        catch (Exception ex) when (ex is not UnsupportedReportNamespaceException)
        {
            // Nothing read off the document survives: the format is null, and Parse treats
            // that as unreadable once DmarcRua has had its own go at the original bytes.
            xmlStream.Position = 0;
            var original = new MemoryStream();
            xmlStream.CopyTo(original);
            original.Position = 0;
            return new NormalizedReport(original, Array.Empty<string?>(), null, null);
        }
    }
}
