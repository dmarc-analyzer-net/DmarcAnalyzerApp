namespace DmarcAnalyzer.Api.Application.Analytics;

public interface ITlsRptRecordChecker
{
    /// <summary>
    /// Live `_smtp._tls.{domain}` TXT lookup. One query, no fetch and no tree
    /// walk — TLS-RPT is a single record and nothing else.
    /// </summary>
    Task<TlsRptRecordDto> CheckAsync(string domainName, CancellationToken ct, bool bypassCache = false);
}

/// <summary>
/// TLS-RPT (RFC 8460 §3) record checking. The parser is a public static so it
/// can be unit-tested without DNS, the same shape as
/// <see cref="RecordInspectionService.ParseDmarc"/> and MtaStsCheckService's
/// record parser.
/// </summary>
public sealed class TlsRptRecordChecker(IDnsTxtResolver dns) : ITlsRptRecordChecker
{
    /// <summary>The version tag, spelled as RFC 8460's ABNF does — %s means case-sensitive.</summary>
    private const string TlsRptVersion = "v=TLSRPTv1";


    public async Task<TlsRptRecordDto> CheckAsync(
        string domainName, CancellationToken ct, bool bypassCache = false)
        => Parse(await dns.ResolveAsync($"_smtp._tls.{domainName}", ct, bypassCache));

    /// <summary>
    /// Parses the `_smtp._tls` answer set. RFC 8460 §3 spells out the same
    /// not-exactly-one rule MTA-STS has: discard records that don't begin with
    /// v=TLSRPTv1, and if what remains isn't a single record, senders "MUST
    /// assume the recipient domain does not implement TLSRPT". That is reported
    /// as <c>invalid</c> rather than <c>found</c>, because it is how reporters
    /// behave — the domain gets no reports either way.
    /// <para>
    /// Detection and validation are deliberately split. Anything whose first
    /// token looks like a TLS-RPT version is *detected*, however malformed, and
    /// then graded — so a domain that tried and got it wrong reads as invalid
    /// with a reason, not as a domain that never published anything. Only a name
    /// carrying no TLS-RPT-shaped record at all is missing.
    /// </para>
    /// </summary>
    public static TlsRptRecordDto Parse(IReadOnlyList<string>? txts)
    {
        if (txts is null)
        {
            return new TlsRptRecordDto(TlsRptRecordStatus.LookupFailed, null, [],
                ["DNS lookup failed — could not check the record."]);
        }

        var records = txts.Where(LooksLikeTlsRptRecord).ToList();
        if (records.Count == 0)
        {
            // Deliberately no issue text: publishing TLS-RPT is optional, and the
            // card renders this state by status alone — it is the ordinary case,
            // not a finding.
            return new TlsRptRecordDto(TlsRptRecordStatus.Missing, null, [], []);
        }

        if (records.Count > 1)
        {
            return new TlsRptRecordDto(TlsRptRecordStatus.Invalid, records[0], [],
                [$"{records.Count} TLS-RPT records published — reporters discard all of them and treat " +
                 "this domain as not implementing TLS-RPT. Remove the extras."]);
        }

        var raw = records[0];

        // The version must be the whole first semicolon-delimited tag, spelled
        // exactly: RFC 8460's ABNF writes it %s"v=TLSRPTv1", and RFC 7405's %s
        // means case-sensitive. A record failing this is discarded by reporters,
        // so calling it found would be the very mistake this card exists to
        // prevent — but it is still a record someone published, so it is graded
        // rather than ignored.
        var firstTag = raw.Trim().Split(';')[0].Trim();
        if (!string.Equals(firstTag, TlsRptVersion, StringComparison.Ordinal))
        {
            return new TlsRptRecordDto(TlsRptRecordStatus.Invalid, raw, [],
                [string.Equals(firstTag, TlsRptVersion, StringComparison.OrdinalIgnoreCase)
                    ? $"The version tag is spelled {firstTag} — RFC 8460 defines it as case-sensitive " +
                      $"{TlsRptVersion}, and reporters discard anything else."
                    : $"The record starts with \"{firstTag}\" — RFC 8460 requires exactly {TlsRptVersion} " +
                      "as the first tag, before any other field."]);
        }

        var tags = ParseTags(raw);

        if (!tags.TryGetValue("rua", out var rua) || rua.Length == 0)
        {
            // A miscased rua is not an absent one, and saying so is the difference
            // between a fix that takes a second and a client wondering what is
            // missing. Reporters read it as absent either way: RFC 8460 writes the
            // directive %s"rua=", and §3 has parsers ignore fields they don't know.
            var miscased = tags.Keys.FirstOrDefault(k => string.Equals(k, "rua", StringComparison.OrdinalIgnoreCase));
            return new TlsRptRecordDto(TlsRptRecordStatus.Invalid, raw, [],
                [miscased is not null
                    ? $"The report destination is spelled {miscased}= — RFC 8460 defines it as " +
                      "case-sensitive rua=, so reporters read this record as having nowhere to send to."
                    : "The record has no rua= destination — RFC 8460 requires one, and there is nowhere " +
                      "to send the reports."]);
        }

        var issues = new List<string>();
        var destinations = new List<string>();
        foreach (var uri in rua.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Only mailto: and https: are defined, and the value has to be a URI
            // a reporter could actually deliver to. A destination failing either
            // is a finding, not a destination — counting it would tell the client
            // reporters were invited when nothing can reach them.
            if (IsDeliverableRuaUri(uri))
            {
                destinations.Add(uri);
            }
            else
            {
                issues.Add($"rua destination {uri} is not one reporters can use — RFC 8460 defines " +
                           "only mailto: and https:, and the value must be a complete URI.");
            }
        }

        if (destinations.Count == 0)
        {
            return new TlsRptRecordDto(TlsRptRecordStatus.Invalid, raw, [], issues);
        }

        return new TlsRptRecordDto(TlsRptRecordStatus.Found, raw, destinations, issues);
    }

    /// <summary>
    /// Whether a rua value is a destination a reporter could deliver to: an
    /// absolute URI in one of the two defined schemes, with the part that does
    /// the delivering actually present. A prefix check alone is not enough —
    /// "https:report" and "mailto:nobody" both pass that and reach nothing.
    /// </summary>
    private static bool IsDeliverableRuaUri(string value)
    {
        if (value.Any(char.IsWhiteSpace) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, "https", StringComparison.Ordinal))
        {
            return uri.Host.Length > 0;
        }

        if (!string.Equals(uri.Scheme, "mailto", StringComparison.Ordinal))
        {
            return false;
        }

        // Uri normalizes mailto into UserInfo@Host, but only for a well-formed
        // address; check both halves rather than trusting the scheme alone.
        return uri.UserInfo.Length > 0 && uri.Host.Length > 0;
    }

    /// <summary>
    /// Semicolon-separated k=v pairs; first occurrence wins. Keys are kept as
    /// published and compared case-sensitively, because RFC 8460's ABNF spells
    /// the directives with %s and §3 has parsers ignore fields they don't
    /// recognize — so RUA= reaches no reporter. (The MTA-STS parser next door
    /// still folds case on its tags; that is its own record's question, and not
    /// something to change from here.)
    /// </summary>
    private static Dictionary<string, string> ParseTags(string record)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in record.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            tags.TryAdd(part[..eq].Trim(), part[(eq + 1)..].Trim());
        }

        return tags;
    }

    /// <summary>
    /// Whether a TXT string is TLS-RPT-shaped enough to grade. Deliberately
    /// looser than the ABNF — case-insensitive, and a bare version counts — so
    /// that a botched record is reported as broken rather than silently
    /// disappearing into "not configured". <see cref="Parse"/> applies the
    /// strict rules; this only decides which records it applies them to.
    /// </summary>
    private static bool LooksLikeTlsRptRecord(string txt)
    {
        var trimmed = txt.Trim();
        if (!trimmed.StartsWith(TlsRptVersion, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The token has to end here: "v=TLSRPTv12" is some other version of some
        // other thing, and claiming it would misreport a record we don't understand.
        return trimmed.Length == TlsRptVersion.Length
            || trimmed[TlsRptVersion.Length] is ';' or ' ' or '\t';
    }
}
