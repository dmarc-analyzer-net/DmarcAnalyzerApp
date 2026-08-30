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
    /// The discard rule is applied strictly, and only then, if nothing survives
    /// it, is anything TLS-RPT-shaped graded for a diagnostic. Both halves
    /// matter: strict first, so a stale malformed record beside a good one
    /// cannot make a working domain read as broken; lenient after, so a domain
    /// that published something botched doesn't read as one that published
    /// nothing. Only a name carrying no TLS-RPT-shaped record at all is missing.
    /// </para>
    /// </summary>
    public static TlsRptRecordDto Parse(IReadOnlyList<string>? txts)
    {
        if (txts is null)
        {
            return new TlsRptRecordDto(TlsRptRecordStatus.LookupFailed, null, [],
                ["DNS lookup failed — could not check the record."]);
        }

        // The RFC's discard step first, and strictly: only records beginning
        // with "v=TLSRPTv1;" are the ones the exactly-one rule counts. Doing
        // this leniently would let a stale malformed record sitting beside a
        // good one report the domain as broken, when reporters discard the
        // stale one and use the good one — the opposite of the mistake this
        // parser is trying not to make.
        var records = txts.Where(IsTlsRptRecord).ToList();

        if (records.Count > 1)
        {
            return new TlsRptRecordDto(TlsRptRecordStatus.Invalid, records[0], [],
                [$"{records.Count} TLS-RPT records published — reporters discard all of them and treat " +
                 "this domain as not implementing TLS-RPT. Remove the extras."]);
        }

        if (records.Count == 0)
        {
            return DescribeWithoutAValidRecord(txts);
        }

        var raw = records[0];
        var tags = ParseTags(raw);

        if (!tags.TryGetValue("rua", out var rua) || rua.Length == 0)
        {
            // Three ways to have no destination, and they need different fixes,
            // so they get different messages. A miscased directive in particular
            // is not an absent one — reporters read it as absent (RFC 8460 writes
            // it %s"rua=", and §3 has parsers ignore fields they don't know), but
            // "you have no rua=" is a strange thing to read about a record whose
            // next word is RUA. Note the exact key is excluded from that search:
            // a present-but-empty rua= is spelled correctly.
            var miscased = tags.Keys.FirstOrDefault(k =>
                !string.Equals(k, "rua", StringComparison.Ordinal)
                && string.Equals(k, "rua", StringComparison.OrdinalIgnoreCase));

            var issue = rua is { Length: 0 }
                ? "The rua= directive is empty — RFC 8460 requires a destination, and there is " +
                  "nowhere to send the reports."
                : miscased is not null
                    ? $"The report destination is spelled {miscased}= — RFC 8460 defines it as " +
                      "case-sensitive rua=, so reporters read this record as having nowhere to send to."
                    : "The record has no rua= destination — RFC 8460 requires one, and there is " +
                      "nowhere to send the reports.";

            return new TlsRptRecordDto(TlsRptRecordStatus.Invalid, raw, [], [issue]);
        }

        var issues = new List<string>();
        var destinations = new List<string>();

        // Empty elements are kept rather than dropped: RFC 8460 requires a URI
        // after every comma, so "rua=mailto:a@example.com," is a syntax error a
        // strict reporter may reject the whole record over. Silently trimming it
        // would report the record as clean.
        foreach (var uri in rua.Split(',', StringSplitOptions.TrimEntries))
        {
            // Only mailto: and https: are defined, and the value has to be a URI
            // a reporter could actually deliver to. A destination failing either
            // is a finding, not a destination — counting it would tell the client
            // reporters were invited when nothing can reach them.
            if (IsDeliverableRuaUri(uri))
            {
                destinations.Add(uri);
            }
            else if (uri.Length == 0)
            {
                issues.Add("The rua list has an empty entry — RFC 8460 requires a URI after every " +
                           "comma. Remove the stray separator.");
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
    /// The RFC's discard rule, read off the ABNF rather than its prose summary:
    /// the record *starts* at the version tag (there is no *WSP before
    /// tlsrpt-version, so leading whitespace is not part of the grammar), the
    /// version is case-sensitive (%s"v=TLSRPTv1"), and at least one delimited
    /// field must follow. The delimiter itself is <c>*WSP ";" *WSP</c>, so
    /// "v=TLSRPTv1 ; rua=…" is legal and a plain StartsWith("v=TLSRPTv1;")
    /// would wrongly discard it.
    /// <para>
    /// So this counts "v=TLSRPTv1;…" and "v=TLSRPTv1 ; …", and does not count a
    /// bare version, a miscased one, "v=TLSRPTv1 junk;…", or anything with
    /// leading whitespace. Those still get graded, just not counted — see
    /// <see cref="DescribeWithoutAValidRecord"/>. Keeping them out of the count
    /// is what stops a stale malformed record from invalidating a working one.
    /// </para>
    /// </summary>
    private static bool IsTlsRptRecord(string txt)
    {
        if (!txt.StartsWith(TlsRptVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = txt.AsSpan(TlsRptVersion.Length).TrimStart(" \t");
        return rest.Length > 0 && rest[0] == ';';
    }

    /// <summary>
    /// Why there is no usable record. Publishing TLS-RPT is optional, so nothing
    /// at all is <c>missing</c> and renders quietly by status alone — but a
    /// record that was published and botched must not read the same way, or the
    /// card tells someone who tried that they never did. So anything
    /// TLS-RPT-shaped is graded here, however malformed.
    /// </summary>
    private static TlsRptRecordDto DescribeWithoutAValidRecord(IReadOnlyList<string> txts)
    {
        var candidate = txts.FirstOrDefault(LooksLikeTlsRptRecord);
        if (candidate is null)
        {
            return new TlsRptRecordDto(TlsRptRecordStatus.Missing, null, [], []);
        }

        // Leading whitespace is checked before the tag is read, because reading
        // the tag trims it away — and "the record is v=TLSRPTv1 and nothing
        // else" is a confusing thing to say about a record that has a rua and
        // one stray space in front of it.
        if (candidate.Length > 0 && char.IsWhiteSpace(candidate[0]))
        {
            return new TlsRptRecordDto(TlsRptRecordStatus.Invalid, candidate, [],
                [$"The record begins with whitespace — RFC 8460's grammar starts it at {TlsRptVersion}, " +
                 "so reporters do not recognize it. Republish the value without the leading space."]);
        }

        var firstTag = FirstTag(candidate);
        var issue = string.Equals(firstTag, TlsRptVersion, StringComparison.Ordinal)
            ? $"The record is {TlsRptVersion} and nothing else — RFC 8460 requires at least a rua= " +
              "destination after it, so reporters have nowhere to send anything."
            : string.Equals(firstTag, TlsRptVersion, StringComparison.OrdinalIgnoreCase)
                ? $"The version tag is spelled {firstTag} — RFC 8460 defines it as case-sensitive " +
                  $"{TlsRptVersion}, and reporters discard anything else."
                : $"The record starts with \"{firstTag}\" — RFC 8460 requires exactly {TlsRptVersion} " +
                  "as the first tag, before any other field.";

        return new TlsRptRecordDto(TlsRptRecordStatus.Invalid, candidate, [], [issue]);
    }

    /// <summary>
    /// Whether a TXT string is TLS-RPT-shaped enough to be worth a diagnostic.
    /// Deliberately looser than <see cref="IsTlsRptRecord"/> — case-insensitive,
    /// and trailing junk on the version field counts — because its only job is
    /// to recognize an attempt. It never decides whether a record is usable.
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

    /// <summary>The first semicolon-delimited tag, trimmed — where the version has to be.</summary>
    private static string FirstTag(string txt) => txt.Trim().Split(';')[0].Trim();
}
