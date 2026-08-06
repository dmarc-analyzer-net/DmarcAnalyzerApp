namespace DmarcAnalyzer.Api.Application.Reports;

/// <summary>
/// Buckets an RFC 8460 result-type into the category the analytics group by.
/// Separate from the parser on purpose: the parser reads what the RFC says, the
/// classification is this app's policy — above all the sts bucket, which is
/// what gates promoting an MTA-STS policy from testing to enforce ("is my
/// policy breaking delivery, or is a receiving MX misconfigured?").
/// </summary>
public static class TlsRptFailureClassifier
{
    public const string Sts = "sts";
    public const string Dane = "dane";
    public const string Transport = "transport";
    public const string Other = "other";

    /// <summary>
    /// validation-failure sits in sts deliberately: RFC 8460 defines it as the
    /// catch-all certificate-validation failure, and certificate validation is
    /// exactly what enforce mode turns from a report line into refused delivery.
    /// For a gate whose failure asymmetry is "a false not-ready is cheap, a
    /// false ready breaks mail", the conservative bucket is correct — and the
    /// raw result type is stored per row, so re-bucketing later is an UPDATE.
    /// RFC 8460 has no closed registry, so unknown values land in other rather
    /// than throwing.
    /// </summary>
    public static string Categorize(string resultType) => resultType.Trim().ToLowerInvariant() switch
    {
        "sts-policy-fetch-error" or "sts-policy-invalid" or "sts-webpki-invalid"
            or "validation-failure" => Sts,
        "tlsa-invalid" or "dnssec-invalid" or "dane-required" => Dane,
        "starttls-not-supported" or "certificate-host-mismatch" or "certificate-expired"
            or "certificate-not-trusted" => Transport,
        _ => Other,
    };
}
