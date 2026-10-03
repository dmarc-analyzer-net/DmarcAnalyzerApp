using System.Globalization;

namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>
/// Number and date wording shared by the digest's text and HTML. Invariant culture on
/// purpose: the worker's culture is whatever the container has, and a digest should
/// read the same from every install.
/// </summary>
public static class DigestFormat
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>
    /// A pass rate. Truncated rather than rounded, so a domain with any failure never
    /// reads 100% and a rate just under a threshold never reads as on it; two decimals
    /// from 99% up, where one decimal would hide the difference that matters.
    /// </summary>
    public static string Percent(double rate)
    {
        if (rate >= 1)
        {
            return "100%";
        }

        return rate >= 0.99
            ? (Math.Floor(rate * 10000) / 100).ToString("0.00", C) + "%"
            : (Math.Floor(rate * 1000) / 10).ToString("0.0", C) + "%";
    }

    public static string Count(long value) => value.ToString("N0", C);

    /// <summary>A threshold or a difference: up to two decimals, so 99.95 never prints as 100.</summary>
    public static string Number(double value) => value.ToString("0.##", C);

    /// <summary>"p=reject", or what stands in for a policy when there is none to name.</summary>
    public static string Policy(string policy) => policy switch
    {
        "missing" => "no DMARC record",
        "unknown" => "policy unknown",
        _ => $"p={policy}",
    };

    /// <summary>The change in pass rate against the previous month, in percentage points.</summary>
    public static string Change(double rate, double? previous)
    {
        if (previous is not { } p)
        {
            return "no data last month";
        }

        var points = (rate - p) * 100;
        return Math.Abs(points) < 0.05
            ? "level"
            : (points > 0 ? "+" : "−") + Math.Abs(points).ToString("0.0", C) + " pts";
    }

    /// <summary>"September 2026".</summary>
    public static string Month(DateTime periodStart) => periodStart.ToString("MMMM yyyy", C);

    /// <summary>"1–30 September 2026".</summary>
    public static string Range(DateTime periodStart, DateTime periodEnd)
        => $"{periodStart.Day}–{periodEnd.AddDays(-1).ToString("d MMMM yyyy", C)}";

    public static string Date(DateTime value) => value.ToString("d MMM", C);

    public static string Plural(long count, string one, string many) => count == 1 ? one : many;
}
