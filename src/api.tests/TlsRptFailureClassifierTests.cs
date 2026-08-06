using DmarcAnalyzer.Api.Application.Reports;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The STS-vs-transport split is what will gate promoting an MTA-STS policy to
/// enforce, so each bucket is pinned here — including validation-failure landing
/// in sts (conservative: a false not-ready is cheap, a false ready breaks mail)
/// and unknown types landing in other rather than throwing.
/// </summary>
public sealed class TlsRptFailureClassifierTests
{
    [Theory]
    [InlineData("sts-policy-fetch-error", TlsRptFailureClassifier.Sts)]
    [InlineData("sts-policy-invalid", TlsRptFailureClassifier.Sts)]
    [InlineData("sts-webpki-invalid", TlsRptFailureClassifier.Sts)]
    [InlineData("validation-failure", TlsRptFailureClassifier.Sts)]
    [InlineData("tlsa-invalid", TlsRptFailureClassifier.Dane)]
    [InlineData("dnssec-invalid", TlsRptFailureClassifier.Dane)]
    [InlineData("dane-required", TlsRptFailureClassifier.Dane)]
    [InlineData("starttls-not-supported", TlsRptFailureClassifier.Transport)]
    [InlineData("certificate-host-mismatch", TlsRptFailureClassifier.Transport)]
    [InlineData("certificate-expired", TlsRptFailureClassifier.Transport)]
    [InlineData("certificate-not-trusted", TlsRptFailureClassifier.Transport)]
    [InlineData("quantum-handshake-flux", TlsRptFailureClassifier.Other)]
    [InlineData("", TlsRptFailureClassifier.Other)]
    [InlineData("  STS-Policy-Invalid ", TlsRptFailureClassifier.Sts)] // trim + case
    public void Buckets(string resultType, string expected)
        => Assert.Equal(expected, TlsRptFailureClassifier.Categorize(resultType));
}
