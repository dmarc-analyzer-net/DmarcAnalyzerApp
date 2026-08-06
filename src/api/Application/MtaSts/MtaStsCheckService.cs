using DmarcAnalyzer.Api.Application.Analytics;

namespace DmarcAnalyzer.Api.Application.MtaSts;

public interface IMtaStsCheckService
{
    /// <summary>
    /// One full live check: `_mta-sts.{domain}` TXT, then (only when a single
    /// valid record exists) the policy fetch and the MX cross-check in parallel.
    /// Stateless and side-effect free — persistence is the state cache's job.
    /// </summary>
    Task<MtaStsCheckResult> CheckAsync(string domainName, CancellationToken ct);
}

/// <summary>
/// MTA-STS (RFC 8461) checking. The parsers are public statics so they can be
/// unit-tested without DNS, HTTP or a database — same shape as
/// <see cref="RecordInspectionService.ParseDmarc"/>.
/// </summary>
public sealed class MtaStsCheckService(
    IDnsTxtResolver txtResolver,
    IDnsMxResolver mxResolver,
    IMtaStsPolicyFetcher policyFetcher) : IMtaStsCheckService
{
    public async Task<MtaStsCheckResult> CheckAsync(string domainName, CancellationToken ct)
    {
        var txts = await txtResolver.ResolveAsync($"_mta-sts.{domainName}", ct);
        var record = ParseStsRecord(txts);

        // No usable TXT record means senders look no further, so neither do we —
        // this keeps the common no-MTA-STS domain at one DNS query per pass.
        if (record.Status != MtaStsRecordStatus.Found)
        {
            return new MtaStsCheckResult(record, null, null, null, null, null, record.Issues);
        }

        var fetchTask = policyFetcher.FetchAsync(domainName, ct);
        var mxTask = mxResolver.ResolveAsync(domainName, ct);
        await Task.WhenAll(fetchTask, mxTask);

        var fetch = await fetchTask;
        var mxHosts = await mxTask;

        var policy = fetch.Status == MtaStsFetchStatus.Ok && fetch.Body is not null
            ? ParsePolicy(fetch.Body)
            : null;

        var mxLookupStatus = mxHosts switch
        {
            null => MtaStsMxStatus.LookupFailed,
            { Count: 0 } => MtaStsMxStatus.Missing,
            _ => MtaStsMxStatus.Found,
        };

        // The DnsMxResolver strips trailing dots, so an RFC 7505 null MX (".")
        // arrives as an empty host — real, but not a deliverable exchange.
        var deliverableMx = mxHosts?.Where(h => h.Host.Length > 0).ToArray();
        var hasNullMx = mxHosts is not null && mxHosts.Any(h => h.Host.Length == 0);

        IReadOnlyList<string>? unmatched = null;
        if (policy is { Valid: true } && policy.Mode != "none"
            && mxLookupStatus == MtaStsMxStatus.Found && deliverableMx is { Length: > 0 })
        {
            unmatched = deliverableMx
                .Select(h => h.Host)
                .Where(h => !policy.MxPatterns.Any(p => MatchesMxPattern(p, h)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var issues = ComposeIssues(record, fetch, policy, mxLookupStatus, hasNullMx, unmatched);
        return new MtaStsCheckResult(record, fetch, policy, mxLookupStatus, mxHosts, unmatched, issues);
    }

    private static IReadOnlyList<string> ComposeIssues(
        MtaStsRecordParseResult record,
        MtaStsPolicyFetchResult fetch,
        MtaStsPolicyParseResult? policy,
        string mxLookupStatus,
        bool hasNullMx,
        IReadOnlyList<string>? unmatched)
    {
        var issues = new List<string>(record.Issues);

        if (fetch.Status != MtaStsFetchStatus.Ok)
        {
            issues.Add($"Could not fetch the policy file: {fetch.Detail ?? fetch.Status}");
        }
        else if (fetch.ContentType is not null
                 && !string.Equals(fetch.ContentType, "text/plain", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(
                $"The policy file is served as {fetch.ContentType} — RFC 8461 expects text/plain. " +
                "Most senders tolerate this, but fix the content type when convenient.");
        }

        if (policy is not null)
        {
            issues.AddRange(policy.Issues);
        }

        var enforcing = policy is { Valid: true, Mode: "enforce" };
        if (policy is { Valid: true } && policy.Mode != "none")
        {
            switch (mxLookupStatus)
            {
                case MtaStsMxStatus.LookupFailed:
                    issues.Add("MX lookup failed — could not cross-check the policy's mx patterns against live MX.");
                    break;
                case MtaStsMxStatus.Missing when !hasNullMx:
                    issues.Add("No MX records published — the policy's mx patterns cover nothing that receives mail.");
                    break;
            }

            if (hasNullMx)
            {
                issues.Add(
                    "This domain publishes a null MX (RFC 7505 — it receives no mail), yet advertises an " +
                    "MTA-STS policy. One of the two is stale.");
            }
        }

        foreach (var host in unmatched ?? [])
        {
            issues.Add(enforcing
                ? $"MX host {host} is not covered by any mx pattern — conforming senders will refuse to " +
                  "deliver via this host. This is the classic MX-migration failure; update the policy."
                : $"MX host {host} is not covered by any mx pattern. Under testing mode senders still " +
                  "deliver and report the mismatch via TLS-RPT; fix it before moving to enforce.");
        }

        return issues;
    }

    // --- MTA-STS TXT record (RFC 8461 §3.1) ---

    /// <summary>
    /// Parses the `_mta-sts` TXT answer set. Records not beginning with v=STSv1
    /// are discarded; after that, anything other than exactly one syntactically
    /// valid record means the domain has no available policy — reported as
    /// `invalid` rather than `found`, because that is how senders behave.
    /// </summary>
    public static MtaStsRecordParseResult ParseStsRecord(IReadOnlyList<string>? txts)
    {
        if (txts is null)
        {
            return new MtaStsRecordParseResult(MtaStsRecordStatus.LookupFailed, null, null,
                ["DNS lookup failed — could not check the record."]);
        }

        var records = txts.Where(IsStsRecord).ToList();
        if (records.Count == 0)
        {
            // Deliberately no issue text: publishing MTA-STS is optional, and the
            // panel renders this state quietly by status alone.
            return new MtaStsRecordParseResult(MtaStsRecordStatus.Missing, null, null, []);
        }

        if (records.Count > 1)
        {
            return new MtaStsRecordParseResult(MtaStsRecordStatus.Invalid, records[0], null,
                [$"{records.Count} MTA-STS records published — senders treat this as having no policy at all. Remove the extras."]);
        }

        var raw = records[0];
        var tags = ParseTags(raw);

        if (!tags.TryGetValue("id", out var id) || id.Length == 0)
        {
            return new MtaStsRecordParseResult(MtaStsRecordStatus.Invalid, raw, null,
                ["The record has no id= tag — senders cannot tell when the policy changes and treat the record as invalid."]);
        }

        if (id.Length > 32 || !id.All(char.IsAsciiLetterOrDigit))
        {
            return new MtaStsRecordParseResult(MtaStsRecordStatus.Invalid, raw, id,
                [$"id={id} is not a valid policy id — RFC 8461 allows 1 to 32 letters and digits."]);
        }

        return new MtaStsRecordParseResult(MtaStsRecordStatus.Found, raw, id, []);
    }

    /// <summary>Semicolon-separated k=v pairs; first occurrence wins, matching receiver behavior for duplicates.</summary>
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

    /// <summary>v=STSv1 must be the first tag; tolerate surrounding whitespace and a bare version-only record.</summary>
    private static bool IsStsRecord(string txt)
    {
        var trimmed = txt.TrimStart();
        if (!trimmed.StartsWith("v=STSv1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // "v=STSv12" is some other thing; "v=STSv1", "v=STSv1;" and "v=STSv1 ;" are ours.
        return trimmed.Length == 7 || trimmed[7] is ';' or ' ' or '\t';
    }

    // --- Policy file (RFC 8461 §3.2) ---

    /// <summary>
    /// Parses the policy file body. Lenient where the wild is lenient (LF or
    /// CRLF, whitespace around the colon, key case) and strict where senders are
    /// strict (required fields, known mode values, mx unless mode is none).
    /// </summary>
    public static MtaStsPolicyParseResult ParsePolicy(string body)
    {
        var issues = new List<string>();
        string? version = null;
        string? mode = null;
        string? maxAgeRaw = null;
        string? firstKey = null;
        var mxPatterns = new List<string>();

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                issues.Add($"Unrecognized line \"{Truncate(line)}\" — expected key: value.");
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            firstKey ??= key;

            switch (key)
            {
                case "version":
                    TakeScalar(ref version, key, value, issues);
                    break;
                case "mode":
                    TakeScalar(ref mode, key, value, issues);
                    break;
                case "max_age":
                    TakeScalar(ref maxAgeRaw, key, value, issues);
                    break;
                case "mx":
                    if (value.Length > 0)
                    {
                        mxPatterns.Add(value.TrimEnd('.').ToLowerInvariant());
                    }
                    break;
                default:
                    // Extension fields are legal; ignore.
                    break;
            }
        }

        var valid = true;

        if (version is null || !string.Equals(version, "STSv1", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(version is null
                ? "Policy has no version field — senders reject it. Add version: STSv1 as the first line."
                : $"version: {version} is not STSv1 — senders reject the policy.");
            valid = false;
        }
        else if (firstKey != "version")
        {
            issues.Add("version is not the first field. Senders parse the fields regardless, but the RFC's format puts it first.");
        }

        string? normalizedMode = null;
        if (mode is null)
        {
            issues.Add("Policy has no mode field — senders reject it. Use testing while rolling out, enforce when clean.");
            valid = false;
        }
        else
        {
            normalizedMode = mode.ToLowerInvariant();
            if (normalizedMode is not ("enforce" or "testing" or "none"))
            {
                issues.Add($"mode: {mode} is not a valid mode — the only legal values are enforce, testing and none.");
                normalizedMode = null;
                valid = false;
            }
        }

        long? maxAge = null;
        if (maxAgeRaw is null)
        {
            issues.Add("Policy has no max_age field — senders reject it.");
            valid = false;
        }
        else if (!long.TryParse(maxAgeRaw, out var parsedMaxAge) || parsedMaxAge < 0)
        {
            issues.Add($"max_age: {maxAgeRaw} is not a number of seconds — senders reject the policy.");
            valid = false;
        }
        else
        {
            maxAge = parsedMaxAge;
            if (parsedMaxAge > 31_557_600)
            {
                issues.Add($"max_age: {parsedMaxAge} exceeds the RFC 8461 maximum of 31557600 — senders cap it there.");
            }
        }

        if (mxPatterns.Count == 0 && normalizedMode != "none")
        {
            issues.Add("Policy lists no mx patterns — required unless mode is none. Senders reject it.");
            valid = false;
        }

        return new MtaStsPolicyParseResult(valid, normalizedMode, maxAge, mxPatterns, issues);
    }

    private static void TakeScalar(ref string? slot, string key, string value, List<string> issues)
    {
        if (slot is null)
        {
            slot = value;
            return;
        }

        // First occurrence wins, matching duplicate-tag handling elsewhere.
        issues.Add($"Duplicate {key} field — the first one wins.");
    }

    private static string Truncate(string line)
        => line.Length <= 60 ? line : line[..57] + "…";

    // --- Policy rendering (the hosted-policy half; the parser above is the other) ---

    /// <summary>
    /// Renders a policy file exactly as the public endpoint serves it: CRLF line
    /// endings (the RFC's ABNF and examples), trailing CRLF, fields in the
    /// RFC's example order, one mx line per pattern (none for an empty list —
    /// legal only under mode none). A round-trip through <see cref="ParsePolicy"/>
    /// is asserted in tests, which is what keeps the serving and checking halves
    /// from ever disagreeing about the format.
    /// </summary>
    public static string RenderPolicyFile(string mode, int maxAgeSeconds, IReadOnlyList<string> mxPatterns)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("version: STSv1\r\n");
        builder.Append("mode: ").Append(mode).Append("\r\n");
        foreach (var pattern in mxPatterns)
        {
            builder.Append("mx: ").Append(pattern).Append("\r\n");
        }

        builder.Append("max_age: ").Append(maxAgeSeconds).Append("\r\n");
        return builder.ToString();
    }

    /// <summary>
    /// Whether a string is a well-formed mx pattern worth persisting: an optional
    /// leading <c>*.</c> (the whole leftmost label), then at least two hostname
    /// labels of letters, digits and interior hyphens. Stricter than the parser,
    /// which reads whatever the wild publishes — this validates what *we* write.
    /// </summary>
    public static bool IsValidMxPattern(string pattern)
    {
        var p = pattern.Trim().TrimEnd('.').ToLowerInvariant();
        if (p.StartsWith("*.", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        if (p.Length is 0 or > 253 || p.Contains('*'))
        {
            return false;
        }

        var labels = p.Split('.');
        return labels.Length >= 2 && labels.All(label =>
            label.Length is >= 1 and <= 63
            && label[0] != '-' && label[^1] != '-'
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    // --- MX pattern matching (RFC 8461 §4.1) ---

    /// <summary>
    /// Whether an mx pattern covers a live MX hostname. A leading `*.` matches
    /// exactly one additional left-most label — `*.example.com` covers
    /// `a.example.com` but neither `example.com` nor `a.b.example.com`.
    /// Comparison is case-insensitive with trailing dots stripped.
    /// </summary>
    public static bool MatchesMxPattern(string pattern, string mxHost)
    {
        var p = pattern.Trim().TrimEnd('.').ToLowerInvariant();
        var host = mxHost.Trim().TrimEnd('.').ToLowerInvariant();
        if (p.Length == 0 || host.Length == 0)
        {
            return false;
        }

        if (!p.StartsWith("*.", StringComparison.Ordinal))
        {
            return string.Equals(p, host, StringComparison.Ordinal);
        }

        var suffix = p[2..];
        if (suffix.Length == 0 || !host.EndsWith("." + suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = host[..(host.Length - suffix.Length - 1)];
        return prefix.Length > 0 && !prefix.Contains('.');
    }
}
