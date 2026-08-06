using System.Text.Json;

namespace DmarcAnalyzer.Api.Application.Reports;

public interface ITlsRptReportParser
{
    /// <summary>
    /// Parses an RFC 8460 JSON report. Throws <see cref="FormatException"/> only
    /// when nothing usable can be stored (not a JSON object, no report-id, no
    /// parseable date range); everything else is tolerated and noted in
    /// <see cref="TlsRptParseResult.ValidationMessages"/>.
    /// </summary>
    TlsRptParseResult Parse(Stream jsonStream);
}

/// <summary>
/// Lenient where the wild is lenient. Reporters disagree with the RFC and with
/// each other: the spec's prose says <c>mx-host-pattern</c> while its own
/// example says <c>mx-host</c> (both occur, as a string or an array), counts
/// arrive as JSON numbers or strings, and RFC 8460 defines no closed registry
/// for result types — so unknown values are stored raw, never thrown on.
/// </summary>
public sealed class TlsRptReportParser : ITlsRptReportParser
{
    public TlsRptParseResult Parse(Stream jsonStream)
    {
        using var document = ParseDocument(jsonStream);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("TLS report is not a JSON object.");
        }

        var messages = new List<string>();

        var reportId = GetString(root, "report-id")?.Trim();
        if (string.IsNullOrEmpty(reportId))
        {
            throw new FormatException("TLS report has no report-id.");
        }

        if (!root.TryGetProperty("date-range", out var dateRange)
            || dateRange.ValueKind != JsonValueKind.Object
            || !TryGetUtc(dateRange, "start-datetime", out var rangeBegin)
            || !TryGetUtc(dateRange, "end-datetime", out var rangeEnd))
        {
            throw new FormatException("TLS report has no parseable date-range.");
        }

        var organizationName = GetString(root, "organization-name")?.Trim() ?? string.Empty;
        if (organizationName.Length == 0)
        {
            messages.Add("organization-name is missing.");
        }

        var contactInfo = GetString(root, "contact-info")?.Trim();

        var policies = new List<TlsRptPolicyParseResult>();
        if (root.TryGetProperty("policies", out var policiesElement)
            && policiesElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var entry in policiesElement.EnumerateArray())
            {
                var policy = ParsePolicy(entry, index++, messages);
                if (policy is not null)
                {
                    policies.Add(policy);
                }
            }
        }
        else
        {
            messages.Add("policies is missing or not an array — the report is stored with no policy rows.");
        }

        return new TlsRptParseResult(
            organizationName, reportId, contactInfo, rangeBegin, rangeEnd, policies, messages);
    }

    private static JsonDocument ParseDocument(Stream jsonStream)
    {
        try
        {
            return JsonDocument.Parse(jsonStream);
        }
        catch (JsonException ex)
        {
            throw new FormatException("TLS report is not valid JSON.", ex);
        }
    }

    private static TlsRptPolicyParseResult? ParsePolicy(
        JsonElement entry, int index, List<string> messages)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            messages.Add($"policies[{index}] is not an object — skipped.");
            return null;
        }

        string? policyType = null;
        string? policyDomain = null;
        string? policyString = null;
        string? mxHostPatterns = null;

        if (entry.TryGetProperty("policy", out var policyElement)
            && policyElement.ValueKind == JsonValueKind.Object)
        {
            policyType = GetString(policyElement, "policy-type")?.Trim().ToLowerInvariant();
            policyDomain = GetString(policyElement, "policy-domain")?.Trim().TrimEnd('.').ToLowerInvariant();
            policyString = JoinStringOrArray(policyElement, "policy-string");

            // The RFC's prose and its own example disagree on the key; both occur.
            mxHostPatterns = JoinStringOrArray(policyElement, "mx-host-pattern")
                ?? JoinStringOrArray(policyElement, "mx-host");
        }

        if (string.IsNullOrEmpty(policyDomain))
        {
            // Without a domain there is nothing to attach the row to — tenancy
            // resolves through it. The report itself is still stored.
            messages.Add($"policies[{index}] has no policy-domain — skipped.");
            return null;
        }

        long successful = 0;
        long failed = 0;
        if (entry.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
        {
            successful = GetCount(summary, "total-successful-session-count", messages) ?? 0;
            failed = GetCount(summary, "total-failure-session-count", messages) ?? 0;
        }
        else
        {
            messages.Add($"policies[{index}] has no summary — session counts stored as zero.");
        }

        var details = new List<TlsRptFailureDetailParseResult>();
        if (entry.TryGetProperty("failure-details", out var failureDetails)
            && failureDetails.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in failureDetails.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // A present detail row asserts at least one failed session; zero
                // would make it invisible in every sum.
                var count = GetCount(detail, "failed-session-count", messages);
                if (count is null)
                {
                    messages.Add($"policies[{index}] has a failure-details row without failed-session-count — stored as 1.");
                }

                details.Add(new TlsRptFailureDetailParseResult(
                    GetString(detail, "result-type")?.Trim().ToLowerInvariant() is { Length: > 0 } resultType
                        ? resultType
                        : "unknown",
                    GetString(detail, "sending-mta-ip"),
                    GetString(detail, "receiving-mx-hostname")?.Trim().TrimEnd('.').ToLowerInvariant(),
                    GetString(detail, "receiving-mx-helo"),
                    GetString(detail, "receiving-ip"),
                    count ?? 1,
                    GetString(detail, "additional-information"),
                    GetString(detail, "failure-reason-code")));
            }
        }
        // A missing failure-details key is a legitimate success-only policy; no message.

        return new TlsRptPolicyParseResult(
            string.IsNullOrEmpty(policyType) ? "unknown" : policyType,
            policyDomain,
            policyString,
            mxHostPatterns,
            successful,
            failed,
            details);
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Counts arrive as numbers or, from some reporters, as strings.</summary>
    private static long? GetCount(JsonElement element, string name, List<string> messages)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetInt64(out var number) && number >= 0:
                return number;
            case JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) && parsed >= 0:
                return parsed;
            default:
                messages.Add($"{name} is not a non-negative count — treated as absent.");
                return null;
        }
    }

    /// <summary>A field the RFC types as an array but the wild also sends bare.</summary>
    private static string? JoinStringOrArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array => string.Join('\n', value.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))),
            _ => null,
        };
    }

    private static bool TryGetUtc(JsonElement element, string name, out DateTime value)
    {
        value = default;
        var raw = GetString(element, name);
        if (raw is null || !DateTimeOffset.TryParse(
                raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        value = parsed.UtcDateTime;
        return true;
    }
}
