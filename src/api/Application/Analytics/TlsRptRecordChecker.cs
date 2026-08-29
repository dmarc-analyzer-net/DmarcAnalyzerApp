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
    /// </summary>
    public static TlsRptRecordDto Parse(IReadOnlyList<string>? txts)
    {
        if (txts is null)
        {
            return new TlsRptRecordDto(TlsRptRecordStatus.LookupFailed, null, [],
                ["DNS lookup failed — could not check the record."]);
        }

        var records = txts.Where(IsTlsRptRecord).ToList();
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
        var tags = ParseTags(raw);

        if (!tags.TryGetValue("rua", out var rua) || rua.Length == 0)
        {
            return new TlsRptRecordDto(TlsRptRecordStatus.Invalid, raw, [],
                ["The record has no rua= destination — RFC 8460 requires one, and there is nowhere " +
                 "to send the reports."]);
        }

        var issues = new List<string>();
        var destinations = new List<string>();
        foreach (var uri in rua.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Only mailto: and https: are defined. Anything else is a destination
            // no reporter will use, so it is a finding rather than a destination.
            if (uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                destinations.Add(uri);
            }
            else
            {
                issues.Add($"rua destination {uri} uses a scheme RFC 8460 does not define — only " +
                           "mailto: and https: are supported, and reporters ignore the rest.");
            }
        }

        if (destinations.Count == 0)
        {
            return new TlsRptRecordDto(TlsRptRecordStatus.Invalid, raw, [], issues);
        }

        return new TlsRptRecordDto(TlsRptRecordStatus.Found, raw, destinations, issues);
    }

    /// <summary>Semicolon-separated k=v pairs; first occurrence wins, matching the MTA-STS parser.</summary>
    private static Dictionary<string, string> ParseTags(string record)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
    /// v=TLSRPTv1 must be the first tag. The RFC's ABNF requires a delimiter and
    /// at least one field after it, so a version-only record is not one of ours —
    /// unlike MTA-STS, where a bare v=STSv1 is still an (invalid) STS record.
    /// </summary>
    private static bool IsTlsRptRecord(string txt)
    {
        var trimmed = txt.TrimStart();
        if (!trimmed.StartsWith("v=TLSRPTv1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // "v=TLSRPTv12" is some other thing; "v=TLSRPTv1;" and "v=TLSRPTv1 ;" are ours.
        return trimmed.Length > 10 && trimmed[10] is ';' or ' ' or '\t';
    }
}
