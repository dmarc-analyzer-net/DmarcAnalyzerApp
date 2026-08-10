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
            .Concat(DescribeDmarcBisTags(policyPublished))
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
            MapAlignment(policyPublished.AdkimRaw),
            MapAlignment(policyPublished.AspfRaw));
    }

    private static string MapDisposition(DispositionType disposition) => disposition switch
    {
        DispositionType.Reject => "reject",
        DispositionType.Quarantine => "quarantine",
        _ => "none",
    };

    /// <summary>
    /// adkim/aspf, read from DmarcRua's raw strings rather than its <c>Adkim</c>/<c>Aspf</c>.
    /// <para>
    /// 2.0.1 replaced those two settable <c>AlignmentType?</c> properties with get-only ones
    /// computed from new <c>AdkimRaw</c>/<c>AspfRaw</c> strings, and the helper behind them
    /// calls <c>Regex.Replace</c> on the raw value with no null check. Both tags are
    /// <c>minOccurs="0"</c> in DmarcRua's own schema, so a reporter that just omits them
    /// leaves the raw string null and merely *reading* the property throws
    /// ArgumentNullException — after deserialization has already succeeded, so it surfaces
    /// here rather than as a parse error. That is 1.5% of the 3241 real reports vendored in
    /// 2.0.1's own test resources, Mail.Ru and Fastmail among them; every report from such a
    /// reporter would fail ingestion outright, where 2.0.0 returned null and fell to the
    /// default below.
    /// </para>
    /// <para>
    /// Reading the raw string keeps 2.0.0's behaviour and does not wait on an upstream fix.
    /// Absent means "relaxed": unlike sp, adkim and aspf have fixed RFC 7489 §6.3 defaults,
    /// so collapsing an absent tag to its default is correct and needs no
    /// HasSubdomainPolicyTag-style presence sniff. Do not simplify this back to
    /// <c>.Adkim</c>/<c>.Aspf</c>.
    /// </para>
    /// <para>
    /// Reported upstream as danielsen/DmarcRua#11. If a later release fixes it, this can
    /// go back to the properties — but check first that an absent tag returns null rather
    /// than throwing, and that the library has not changed what absent *means*: "relaxed"
    /// is this method's decision to make, not the library's.
    /// </para>
    /// <para>
    /// Trimming, lowercasing and dropping non-alphanumerics mirrors what 2.0.1 does to these
    /// values — that much of its change is a real improvement, so '&#160;S&#160;' still reads
    /// as strict instead of silently becoming relaxed.
    /// </para>
    /// </summary>
    private static string MapAlignment(string? alignment)
    {
        var cleaned = (alignment ?? string.Empty)
            .ToLowerInvariant()
            .Where(char.IsAsciiLetterOrDigit)
            .ToArray();

        return cleaned is ['s'] ? "strict" : "relaxed";
    }

    /// <summary>
    /// Read-through only, per the DMARCbis (RFC 9989/9990/9991) impact report: np,
    /// testing and discovery_method are already modeled by DmarcRua 2.0.0 (unlike sp,
    /// these are genuinely nullable, so presence comes from the library for free — no
    /// HasSubdomainPolicyTag-style XML sniff needed) but were never read. Surfaced as
    /// informational validation messages rather than new DmarcReport columns: cheap,
    /// no migration, and there is nowhere else yet worth putting them — the same
    /// unresolved backlog item that leaves ValidationMessages itself unsurfaced to
    /// operators applies here too.
    /// </summary>
    private static IEnumerable<string> DescribeDmarcBisTags(PolicyPublishedType policyPublished)
    {
        if (policyPublished.Np is { } np)
        {
            yield return $"info: np={MapDisposition(np)} published for {policyPublished.Domain}";
        }

        if (policyPublished.Testing is { } testing)
        {
            yield return $"info: t={(testing == TestingType.Yes ? "y" : "n")} published for {policyPublished.Domain}";
        }

        if (policyPublished.DiscoveryMethod is { } discovery)
        {
            var method = discovery == DiscoveryType.Treewalk ? "treewalk" : "psl";
            yield return $"info: discovery_method={method} reported for {policyPublished.Domain}";
        }
    }

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

        // SpfDomainScope. DmarcRua 2.0.0 modelled only mfrom, so helo — legal per RFC 7208
        // and sent by real reporters — was fatal, and this parser rewrote it to mfrom to save
        // the document. 2.0.1 added Helo, so the scope is now recorded as sent; the rewrite
        // was the last thing storing a value the reporter never reported. Kept as a repair
        // rather than dropped outright because the enum still has no empty member, so
        // <scope/> or a value neither of these is fatal, and mfrom is the safe reading —
        // it is the scope RFC 7489 aligns against and the one ~99% of reports send.
        ("spf", "scope", ["mfrom", "helo"], "mfrom"),
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
