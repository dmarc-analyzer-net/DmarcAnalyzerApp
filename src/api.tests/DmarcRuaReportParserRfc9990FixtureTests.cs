using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using DmarcAnalyzer.Api.Application.Reports;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The RFC 9990 test report (step 0 of docs/planning/rfc-9990-plan.md).
/// <para>
/// The first test keeps the fixture honest: it must validate against the RFC's own XSD,
/// so a failure in the parser tests is a parser problem and not a broken fixture.
/// The others pin what the parser does with a conformant RFC 9990 report, including
/// every message it produces. Change them on purpose, one message at a time.
/// </para>
/// </summary>
public sealed class DmarcRuaReportParserRfc9990FixtureTests
{
    private const string FixtureName = "sample-rfc9990-aggregate.xml";
    private const string DmarcBisNamespace = "urn:ietf:params:xml:ns:dmarc-2.0";

    /// <summary>Info, not a warning: it is how every RFC 9990 report is read.</summary>
    private static readonly DmarcReportParseMessage NamespaceMessage =
        DmarcReportParseMessage.Info("stripped XML namespace 'urn:ietf:params:xml:ns:dmarc-2.0' for compatibility");

    private readonly DmarcRuaReportParser _parser = new();

    [Fact]
    public void Fixture_ValidatesAgainstRfc9990Schema()
    {
        var errors = ValidateAgainstRfc9990Schema(XDocument.Load(FixturePath(FixtureName)));

        Assert.Empty(errors);
    }

    /// <summary>
    /// A conformant report, parsed as sent. Every report-level field it carries comes back,
    /// the pct it does not send is null rather than invented, nothing it carries — the
    /// <c>lang</c> attributes, <c>generator</c>, the extensions in both places, the
    /// <c>policy_test_mode</c> override reason — is a schema error or a repair, and the one
    /// message is the namespace strip, which is how an RFC 9990 report is read, not a fault.
    /// </summary>
    [Fact]
    public void Parse_Rfc9990Fixture_ReadsEveryReportLevelField()
    {
        using var stream = File.OpenRead(FixturePath(FixtureName));

        var result = _parser.Parse(stream);

        Assert.Equal("Example Receiver", result.OrganizationName);
        Assert.Equal("rfc9990-fixture-1", result.ReportId);
        Assert.Equal("example.com", result.PolicyDomain);
        Assert.Equal("quarantine", result.PublishedPolicy);
        Assert.Equal("none", result.SubdomainPolicy);
        Assert.Equal("relaxed", result.DkimAlignment);
        Assert.Equal("strict", result.SpfAlignment);
        Assert.Equal(2, result.RecordCount);
        Assert.Equal(["pass", "none"], result.Records.Select(x => x.Disposition));

        Assert.Equal(DmarcReportFormats.Rfc9990, result.ReportFormat);
        Assert.Equal("reject", result.NonexistentSubdomainPolicy);
        Assert.True(result.Testing);
        Assert.Equal("treewalk", result.DiscoveryMethod);
        Assert.Equal("Example DMARC Aggregate Reporter v1.2", result.Generator);
        Assert.Equal(["DNS timeout while fetching the policy for sub.example.com"], result.ReportErrors);

        // The report sends no pct, and none is invented.
        Assert.Null(result.PublishedPct);

        Assert.False(result.HasValidationErrors, string.Join(" | ", result.ValidationMessages));
        // The one message is info, so a conformant report has no warnings either: once
        // messages are shown, the format itself must not read as repaired.
        Assert.False(result.HasValidationWarnings, string.Join(" | ", result.ValidationMessages));
        Assert.Equal([NamespaceMessage], result.ValidationMessages);
    }

    /// <summary>
    /// RFC 9990 added the override type <c>policy_test_mode</c>, which DmarcRua 2.1 does not
    /// model; it used to reach the serializer, which threw and lost the whole report. The
    /// fixture carries it, because RFC 9990 requires a reason when the disposition differs
    /// from the policy, and the test above shows it parses without a message. A value
    /// neither RFC defines is a repair, and says so — the report still parses.
    /// </summary>
    [Fact]
    public void Parse_Rfc9990UnknownOverrideReason_RepairsItWithoutLosingTheReport()
    {
        var document = XDocument.Load(FixturePath(FixtureName));
        XNamespace ns = DmarcBisNamespace;
        document.Descendants(ns + "policy_evaluated").First().Add(
            new XElement(ns + "reason", new XElement(ns + "type", "not_a_reason")));
        using var stream = ToStream(document);

        var result = _parser.Parse(stream);

        Assert.Equal(["pass", "none"], result.Records.Select(x => x.Disposition));
        Assert.False(result.HasValidationErrors, string.Join(" | ", result.ValidationMessages));
        Assert.Equal(
            [
                NamespaceMessage,
                DmarcReportParseMessage.Warning(
                    "replaced unrecognised reason/type value 'not_a_reason' with 'other' for compatibility"),
            ],
            result.ValidationMessages);
    }

    private static MemoryStream ToStream(XDocument document)
    {
        var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Schema errors only. Extension elements are <c>processContents="lax"</c> with no schema
    /// of their own, which raises warnings by design.
    /// </summary>
    private static List<string> ValidateAgainstRfc9990Schema(XDocument document)
    {
        var schemas = new XmlSchemaSet();
        schemas.Add(DmarcBisNamespace, FixturePath("rfc9990.xsd"));

        var errors = new List<string>();
        document.Validate(schemas, (_, e) =>
        {
            if (e.Severity == XmlSeverityType.Error)
            {
                errors.Add(e.Message);
            }
        });
        return errors;
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
