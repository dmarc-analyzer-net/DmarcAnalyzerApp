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
/// The others pin what the parser does with a conformant RFC 9990 report today,
/// including every message it produces. They are expected to change as the plan's PRs
/// land; change them on purpose, one message at a time.
/// </para>
/// </summary>
public sealed class DmarcRuaReportParserRfc9990FixtureTests
{
    private const string FixtureName = "sample-rfc9990-aggregate.xml";
    private const string DmarcBisNamespace = "urn:ietf:params:xml:ns:dmarc-2.0";

    private readonly DmarcRuaReportParser _parser = new();

    [Fact]
    public void Fixture_ValidatesAgainstRfc9990Schema()
    {
        var errors = ValidateAgainstRfc9990Schema(XDocument.Load(FixturePath(FixtureName)));

        Assert.Empty(errors);
    }

    /// <summary>
    /// Today's parser on a conformant report. Every line here is something PR 1 or PR 2
    /// has to remove, reword or turn into a column; see the plan's step 0 findings.
    /// </summary>
    [Fact]
    public void Parse_Rfc9990Fixture_CurrentBehaviour()
    {
        using var stream = File.OpenRead(FixturePath(FixtureName));

        var result = _parser.Parse(stream);

        Assert.Equal("Example Receiver", result.OrganizationName);
        Assert.Equal("rfc9990-fixture-1", result.ReportId);
        Assert.Equal("example.com", result.PolicyDomain);
        Assert.Equal("quarantine", result.PublishedPolicy);
        Assert.Equal("none", result.SubdomainPolicy);
        Assert.Equal(2, result.RecordCount);
        Assert.Equal(["pass", "none"], result.Records.Select(x => x.Disposition));

        // The report sends no pct; 100 is invented (PR 1).
        Assert.Equal(100, result.PublishedPct);

        // A conformant report is flagged as having errors (PR 1).
        Assert.True(result.HasValidationErrors);
        Assert.Equal(
            [
                "error: The 'lang' attribute is not declared.",
                "error: The 'lang' attribute is not declared.",
                "error: The element 'report_metadata' has invalid child element 'generator'. List of possible elements expected: 'org_name, email, extra_contact_info, report_id, date_range, error'.",
                "error: The element 'feedback' has invalid child element 'extension'. List of possible elements expected: 'record'.",
                "warning: stripped XML namespace 'urn:ietf:params:xml:ns:dmarc-2.0' for compatibility",
                "info: np=reject published for example.com",
                "info: t=y published for example.com",
                "info: discovery_method=treewalk reported for example.com",
            ],
            result.ValidationMessages);
    }

    /// <summary>
    /// RFC 9990 added the override type <c>policy_test_mode</c>, which DmarcRua 2.1.0 does not
    /// model, and the parser's value repair does not cover <c>reason/type</c>. The serializer
    /// throws and the whole report is lost. Kept out of the fixture so the test above can run;
    /// PR 1 fixes it and turns this into a parse that succeeds.
    /// </summary>
    [Fact]
    public void Parse_Rfc9990PolicyTestModeReason_CurrentlyThrows()
    {
        var document = XDocument.Load(FixturePath(FixtureName));
        XNamespace ns = DmarcBisNamespace;
        document.Descendants(ns + "policy_evaluated").Last().Add(
            new XElement(ns + "reason",
                new XElement(ns + "type", "policy_test_mode"),
                new XElement(ns + "comment", "t=y published")));
        Assert.Empty(ValidateAgainstRfc9990Schema(document));

        using var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;

        Assert.ThrowsAny<InvalidOperationException>(() => _parser.Parse(stream));
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
