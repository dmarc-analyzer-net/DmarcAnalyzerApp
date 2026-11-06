using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class RecordInspectionTests
{
    // --- DMARC parsing ---

    [Fact]
    public void ParseDmarc_FullRecord_ExtractsTags()
    {
        var dto = RecordInspectionService.ParseDmarc(
            ["v=DMARC1; p=quarantine; sp=none; pct=25; rua=mailto:dmarc@acme.example; adkim=s; aspf=r"]);

        Assert.Equal(RecordLookupStatus.Found, dto.Status);
        Assert.Equal("quarantine", dto.Policy);
        Assert.Equal("none", dto.SubdomainPolicy);
        Assert.Equal(25, dto.Pct);
        Assert.Equal("mailto:dmarc@acme.example", dto.Rua);
        Assert.Equal("s", dto.DkimAlignment);
        Assert.Equal("r", dto.SpfAlignment);
        // sp=none under p=quarantine leaves subdomains unprotected — see
        // ParseDmarc_ExplicitlyWeakSp_IsRaisedAsAnIssue.
        Assert.Equal(
            "sp=none is weaker than p=quarantine — subdomains are not protected at the same level.",
            Assert.Single(dto.Issues));
    }

    /// <summary>
    /// "Tags MUST be interpreted in a case-sensitive manner" (RFC 6376 §3.2, the
    /// tag-value syntax RFC 9989 §4.7 adopts). RUA= reads like a reporting address in a
    /// zone file and is not one to any receiver, so the reports never start and nothing
    /// says why. This parser still reads it, so that the rest of the panel has something
    /// to show, but it does not let it pass unremarked.
    /// </summary>
    [Theory]
    [InlineData("v=DMARC1; p=none; RUA=mailto:dmarc@acme.example", "RUA= is not rua=")]
    [InlineData("v=DMARC1; P=none; rua=mailto:dmarc@acme.example", "P= is not p=")]
    [InlineData("v=DMARC1; p=none; rua=mailto:dmarc@acme.example; SP=reject", "SP= is not sp=")]
    public void ParseDmarc_KnownTagInTheWrongCase_FlagsIssue(string record, string expected)
    {
        var dto = RecordInspectionService.ParseDmarc([record]);

        Assert.Contains(dto.Issues, i => i.StartsWith(expected, StringComparison.Ordinal));
        Assert.Contains(dto.Issues, i => i.Contains("case-sensitive"));
    }

    /// <summary>An unregistered tag is a different matter — RFC 9989 §4.8 has receivers
    /// ignore it, so it is harmless and goes unmentioned.</summary>
    [Fact]
    public void ParseDmarc_UnknownTag_IsNotFlagged()
    {
        var dto = RecordInspectionService.ParseDmarc(
            ["v=DMARC1; p=none; rua=mailto:dmarc@acme.example; x-vendor=acme-7"]);

        Assert.Empty(dto.Issues);
    }

    /// <summary>
    /// "Tags with duplicate names MUST NOT occur within a single tag-list; if a tag name
    /// does occur more than once, the entire tag-list is invalid" (RFC 6376 §3.2) — so
    /// this is not a near-miss, it is a domain with no DMARC at all.
    /// </summary>
    [Fact]
    public void ParseDmarc_RepeatedTagName_FlagsIssue()
    {
        var dto = RecordInspectionService.ParseDmarc(
            ["v=DMARC1; p=none; rua=mailto:a@acme.example; rua=mailto:b@acme.example"]);

        Assert.Contains(dto.Issues, i => i.Contains("rua= appears more than once"));
        Assert.Contains(dto.Issues, i => i.Contains("no DMARC policy at all"));
        // First occurrence still wins for display, so the panel is not left blank.
        Assert.Equal("mailto:a@acme.example", dto.Rua);
    }

    /// <summary>
    /// The version value is case sensitive and "the only possible value is 'DMARC1'"
    /// (RFC 9989 §4.7). The lookup matches it loosely so the record is found and
    /// explained rather than reported as no record at all.
    /// </summary>
    [Theory]
    [InlineData("v=dmarc1; p=reject; rua=mailto:dmarc@acme.example")]
    [InlineData("v=DMARC1x; p=reject; rua=mailto:dmarc@acme.example")]
    public void ParseDmarc_VersionReceiversWouldReject_FlagsIssue(string record)
    {
        var dto = RecordInspectionService.ParseDmarc([record]);

        Assert.Equal(RecordLookupStatus.Found, dto.Status);
        Assert.Contains(dto.Issues, i => i.Contains("is not v=DMARC1"));
    }

    /// <summary>
    /// A mis-cased version tag is the one casing error that costs the whole record: a
    /// receiver looking for a first tag spelled "v" does not find "V", the version tag
    /// is therefore absent, and "the entire record MUST be ignored" (RFC 9989 §4.7).
    /// The generic casing line — receivers ignore this tag — would understate that by
    /// a wide margin, so it is replaced rather than added to.
    /// </summary>
    [Theory]
    [InlineData("V=DMARC1; p=reject; rua=mailto:dmarc@acme.example")]
    [InlineData("V=dmarc1; p=reject; rua=mailto:dmarc@acme.example")]
    public void ParseDmarc_VersionTagInTheWrongCase_SaysTheWholeRecordIsIgnored(string record)
    {
        var dto = RecordInspectionService.ParseDmarc([record]);

        Assert.Equal(RecordLookupStatus.Found, dto.Status);
        Assert.Contains(dto.Issues, i => i.Contains("is not v=DMARC1") && i.Contains("ignore the entire record"));
        Assert.DoesNotContain(dto.Issues, i => i.Contains("ignore this tag entirely"));
        // Still read leniently, so the panel shows what is published rather than nothing.
        Assert.Equal("reject", dto.Policy);
    }

    /// <summary>
    /// An empty rua= is worse than no rua= and reads better: it looks configured, it
    /// silences the missing-rua warning, and it carries none of the "syntactically
    /// valid reporting URI" RFC 9989 §4.10.1 wants before a receiver sends anything.
    /// </summary>
    [Theory]
    [InlineData("v=DMARC1; p=reject; rua=")]
    [InlineData("v=DMARC1; p=reject; rua=   ")]
    public void ParseDmarc_EmptyRua_FlagsIssue(string record)
    {
        var dto = RecordInspectionService.ParseDmarc([record]);

        Assert.Contains(dto.Issues, i => i.Contains("rua= is published with no address"));
    }

    [Fact]
    public void ParseDmarc_SegmentThatIsNotNameEqualsValue_FlagsIssue()
    {
        var dto = RecordInspectionService.ParseDmarc(["v=DMARC1; p=none; rua=mailto:dmarc@acme.example; garbage"]);

        Assert.Contains(dto.Issues, i => i.Contains("\"garbage\" is not a name=value tag"));
    }

    [Fact]
    public void ParseDmarc_MissingRecord_ReportsMissing()
    {
        var dto = RecordInspectionService.ParseDmarc([]);
        Assert.Equal(RecordLookupStatus.Missing, dto.Status);
        Assert.NotEmpty(dto.Issues);
    }

    [Fact]
    public void ParseDmarc_LookupFailure_IsDistinctFromMissing()
    {
        var dto = RecordInspectionService.ParseDmarc(null);
        Assert.Equal(RecordLookupStatus.LookupFailed, dto.Status);
    }

    [Fact]
    public void ParseDmarc_MultipleRecords_FlagsIssue()
    {
        var dto = RecordInspectionService.ParseDmarc(
            ["v=DMARC1; p=none", "v=DMARC1; p=reject"]);
        Assert.Contains(dto.Issues, i => i.Contains("2 DMARC records"));
    }

    [Fact]
    public void ParseDmarc_NoRua_FlagsIssue()
    {
        var dto = RecordInspectionService.ParseDmarc(["v=DMARC1; p=none"]);
        Assert.Contains(dto.Issues, i => i.Contains("rua"));
    }

    // --- RFC 9989 tags: t, psd, np ---

    [Fact]
    public void ParseDmarc_TPsdNp_AreSurfaced()
    {
        var dto = RecordInspectionService.ParseDmarc(
            ["v=DMARC1; p=reject; np=quarantine; rua=mailto:d@acme.example; t=y; psd=n"]);

        Assert.Equal("y", dto.Testing);
        Assert.Equal("n", dto.PublicSuffixDomain);
        Assert.Equal("quarantine", dto.NonExistentSubdomainPolicy);
        Assert.Empty(dto.Issues);
    }

    [Fact]
    public void ParseDmarc_TPsdNp_AbsentByDefault()
    {
        var dto = RecordInspectionService.ParseDmarc(["v=DMARC1; p=reject; rua=mailto:d@acme.example"]);
        Assert.Null(dto.Testing);
        Assert.Null(dto.PublicSuffixDomain);
        Assert.Null(dto.NonExistentSubdomainPolicy);
    }

    [Theory]
    [InlineData("t=x", "Invalid t=")]
    [InlineData("psd=maybe", "Invalid psd=")]
    [InlineData("np=block", "Invalid np=")]
    public void ParseDmarc_InvalidNewTagValue_FlagsIssue(string tag, string expectedPrefix)
    {
        var dto = RecordInspectionService.ParseDmarc([$"v=DMARC1; p=reject; rua=mailto:d@acme.example; {tag}"]);
        Assert.Contains(dto.Issues, i => i.StartsWith(expectedPrefix, StringComparison.Ordinal));
    }

    // --- RFC 9989: p downgraded from required to RECOMMENDED, with a rua-conditioned fallback ---

    [Fact]
    public void ParseDmarc_NoPolicyButValidRua_ExplainsImplicitNone()
    {
        var dto = RecordInspectionService.ParseDmarc(["v=DMARC1; rua=mailto:d@acme.example"]);
        Assert.Contains(dto.Issues, i => i.Contains("treat this as") && i.Contains("p=none"));
        Assert.DoesNotContain(dto.Issues, i => i.Contains("no DMARC processing"));
    }

    [Fact]
    public void ParseDmarc_NoPolicyAndNoRua_ExplainsNoProcessing()
    {
        var dto = RecordInspectionService.ParseDmarc(["v=DMARC1"]);
        Assert.Contains(dto.Issues, i => i.Contains("no DMARC processing"));
    }

    // --- SPF parsing ---

    [Fact]
    public void ParseSpf_CountsLookupsAndFindsAllQualifier()
    {
        var dto = RecordInspectionService.ParseSpf(
            ["v=spf1 include:_spf.google.com include:sendgrid.net a mx ip4:198.51.100.10 -all"]);

        Assert.Equal(RecordLookupStatus.Found, dto.Status);
        Assert.Equal(4, dto.LookupMechanisms); // 2 includes + a + mx (ip4 is free)
        Assert.Equal("-", dto.AllQualifier);
        Assert.Empty(dto.Issues);
    }

    [Fact]
    public void ParseSpf_PlusAll_FlagsIssue()
    {
        var dto = RecordInspectionService.ParseSpf(["v=spf1 +all"]);
        Assert.Contains(dto.Issues, i => i.Contains("+all"));
    }

    [Fact]
    public void ParseSpf_MultipleRecords_FlagsPermerror()
    {
        var dto = RecordInspectionService.ParseSpf(
            ["v=spf1 -all", "v=spf1 include:_spf.google.com ~all"]);
        Assert.Contains(dto.Issues, i => i.Contains("permerror"));
        Assert.Equal(2, dto.RecordCount);
    }

    [Fact]
    public void ParseSpf_IgnoresUnrelatedTxtRecords()
    {
        var dto = RecordInspectionService.ParseSpf(
            ["google-site-verification=abc123", "v=spf1 -all"]);
        Assert.Equal(RecordLookupStatus.Found, dto.Status);
        Assert.Equal(1, dto.RecordCount);
    }

    // --- Service (DNS faked, DB in-memory) ---

    private sealed class FakeDns(Dictionary<string, IReadOnlyList<string>?> answers) : IDnsTxtResolver
    {
        public Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct, bool bypassCache = false)
            => Task.FromResult(answers.GetValueOrDefault(name));
    }

    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    /// <summary>
    /// <paramref name="pct"/> defaults to the 100 an RFC 7489 reporter sends; pass null for an
    /// RFC 9990 report, which carries none. <paramref name="reportFormat"/> null is a row from
    /// before the format was recorded.
    /// </summary>
    private static async Task<Guid> SeedDomainWithReportAsync(
        DmarcAnalyzerDbContext db, string policy, string? subdomainPolicy = null, bool spReported = true,
        string domainName = "acme.example",
        int? pct = 100, string? np = null, bool? testing = null, string? reportFormat = DmarcReportFormats.Rfc7489)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC",
            RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var domain = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = domainName, IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.AddRange(client, domain, new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domain.Id, ReportSourceId = Guid.NewGuid(),
            OrganizationName = "google.com", ReportId = "r1",
            RangeBeginUtc = DateTime.UtcNow.AddDays(-2), RangeEndUtc = DateTime.UtcNow.AddDays(-1),
            RecordCount = 0, IngestedAtUtc = DateTime.UtcNow,
            PublishedPolicy = policy,
            SubdomainPolicy = spReported ? subdomainPolicy ?? policy : null,
            PublishedPct = pct,
            NonexistentSubdomainPolicy = np,
            Testing = testing,
            ReportFormat = reportFormat,
        });
        await db.SaveChangesAsync();
        return domain.Id;
    }

    [Fact]
    public async Task Inspect_ComparesDnsAgainstObservedPolicy()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "none");

        // DNS now says quarantine, but the last report observed none → mismatch on p.
        var dns = new FakeDns(new()
        {
            ["_dmarc.acme.example"] = ["v=DMARC1; p=quarantine; rua=mailto:d@acme.example"],
            ["acme.example"] = ["v=spf1 include:_spf.google.com -all"],
        });

        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Admin(), dns, new DmarcPolicyResolver(dns))
            .InspectAsync(domainId, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(RecordLookupStatus.Found, dto!.Dmarc.Status);
        Assert.Equal("quarantine", dto.Dmarc.Policy);
        Assert.NotNull(dto.Observed);
        Assert.Equal("none", dto.Observed!.Policy);

        var p = dto.Comparison.Single(c => c.Field == "p");
        Assert.Equal(RecordComparisonStatus.Differs, p.Status);
        var pct = dto.Comparison.Single(c => c.Field == "pct");
        Assert.Equal(RecordComparisonStatus.Match, pct.Status);
    }

    /// <summary>
    /// The regression this guards: a record with no sp used to be compared as if it
    /// published sp=p, so any reporter echoing the XSD default sp=none read as a
    /// policy regression on every p=reject domain.
    /// </summary>
    [Theory]
    [InlineData("none")]        // reporter echoed the XSD default
    [InlineData("reject")]      // reporter resolved the inheritance itself
    [InlineData(null)]          // reporter sent no sp at all
    public async Task Inspect_NoSpPublished_IsInheritedNotADifference(string? observedSp)
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(
            db, policy: "reject", subdomainPolicy: observedSp, spReported: observedSp is not null);

        var dns = new FakeDns(new()
        {
            ["_dmarc.acme.example"] = ["v=DMARC1; p=reject; rua=mailto:d@acme.example"],
            ["acme.example"] = ["v=spf1 -all"],
        });

        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Admin(), dns, new DmarcPolicyResolver(dns))
            .InspectAsync(domainId, CancellationToken.None);

        var sp = dto!.Comparison.Single(c => c.Field == "sp");
        Assert.Equal(RecordComparisonStatus.Inherited, sp.Status);
        Assert.Null(sp.Published);
        Assert.Contains("inherit p=reject", sp.Note);
        Assert.DoesNotContain(dto.Comparison, c => c.Status == RecordComparisonStatus.Differs);
    }

    [Fact]
    public async Task Inspect_SpPublishedButNotReported_IsNotADifference()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", spReported: false);

        var dns = new FakeDns(new()
        {
            ["_dmarc.acme.example"] = ["v=DMARC1; p=reject; sp=quarantine; rua=mailto:d@acme.example"],
            ["acme.example"] = ["v=spf1 -all"],
        });

        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Admin(), dns, new DmarcPolicyResolver(dns))
            .InspectAsync(domainId, CancellationToken.None);

        var sp = dto!.Comparison.Single(c => c.Field == "sp");
        Assert.Equal(RecordComparisonStatus.NotReported, sp.Status);
        Assert.Equal("quarantine", sp.Published);
        Assert.Contains("google.com", sp.Note);
    }

    [Fact]
    public async Task Inspect_ExplicitSpDisagreeing_IsStillADifference()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", subdomainPolicy: "none");

        var dns = new FakeDns(new()
        {
            ["_dmarc.acme.example"] = ["v=DMARC1; p=reject; sp=quarantine; rua=mailto:d@acme.example"],
            ["acme.example"] = ["v=spf1 -all"],
        });

        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Admin(), dns, new DmarcPolicyResolver(dns))
            .InspectAsync(domainId, CancellationToken.None);

        var sp = dto!.Comparison.Single(c => c.Field == "sp");
        Assert.Equal(RecordComparisonStatus.Differs, sp.Status);
        Assert.Equal("quarantine", sp.Published);
        Assert.Equal("none", sp.Observed);
    }

    private static FakeDns DnsPublishing(string dmarcRecord) => new(new()
    {
        ["_dmarc.acme.example"] = [dmarcRecord],
        ["acme.example"] = ["v=spf1 -all"],
    });

    private static async Task<RecordInspectionDto> InspectAsync(DmarcAnalyzerDbContext db, Guid domainId, FakeDns dns)
    {
        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Admin(), dns, new DmarcPolicyResolver(dns))
            .InspectAsync(domainId, CancellationToken.None);
        Assert.NotNull(dto);
        return dto;
    }

    // --- pct, np and t (PR 3 of docs/planning/rfc-9990-plan.md) ---

    /// <summary>
    /// RFC 9990 removed pct from the report. Comparing an absent value against an invented
    /// 100 read every pct=50 record as a disagreement with a reporter that said nothing.
    /// </summary>
    [Fact]
    public async Task Inspect_PctNotSentByAnRfc9990Reporter_IsNotReported()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", pct: null, reportFormat: DmarcReportFormats.Rfc9990);

        var dto = await InspectAsync(db, domainId, DnsPublishing("v=DMARC1; p=reject; rua=mailto:d@acme.example"));

        var pct = dto.Comparison.Single(c => c.Field == "pct");
        Assert.Equal(RecordComparisonStatus.NotReported, pct.Status);
        Assert.Equal("100", pct.Published);
        Assert.Null(pct.Observed);
        Assert.Contains("RFC 9990", pct.Note);
        Assert.Null(dto.Observed!.Pct);
        Assert.Equal(DmarcReportFormats.Rfc9990, dto.Observed.ReportFormat);
        Assert.DoesNotContain(dto.Comparison, c => c.Status == RecordComparisonStatus.Differs);
    }

    /// <summary>A record still publishing pct= to RFC 9990 reporters publishes a tag they do not apply.</summary>
    [Fact]
    public async Task Inspect_PctPublishedToAnRfc9990Reporter_IsARecordIssue()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", pct: null, reportFormat: DmarcReportFormats.Rfc9990);

        var dto = await InspectAsync(db, domainId, DnsPublishing("v=DMARC1; p=reject; pct=50; rua=mailto:d@acme.example"));

        Assert.Contains(dto.Dmarc.Issues, i => i.StartsWith("pct=50 is published", StringComparison.Ordinal) && i.Contains("RFC 9989"));
        Assert.Equal(RecordComparisonStatus.NotReported, dto.Comparison.Single(c => c.Field == "pct").Status);
    }

    [Fact]
    public async Task Inspect_PctPublishedToAnRfc7489Reporter_IsComparedAsBefore()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", pct: 100);

        var dto = await InspectAsync(db, domainId, DnsPublishing("v=DMARC1; p=reject; pct=50; rua=mailto:d@acme.example"));

        Assert.DoesNotContain(dto.Dmarc.Issues, i => i.Contains("RFC 9989"));
        var pct = dto.Comparison.Single(c => c.Field == "pct");
        Assert.Equal(RecordComparisonStatus.Differs, pct.Status);
        Assert.Equal("50", pct.Published);
        Assert.Equal("100", pct.Observed);
    }

    /// <summary>np is derived like sp: from sp when published, else from p. Nothing a reporter can contradict.</summary>
    [Theory]
    [InlineData("v=DMARC1; p=reject; rua=mailto:d@acme.example", "p=reject")]
    [InlineData("v=DMARC1; p=reject; sp=quarantine; rua=mailto:d@acme.example", "sp=quarantine")]
    public async Task Inspect_NpNotPublished_IsInherited(string record, string inheritedFrom)
    {
        await using var db = NewDb();
        // sp echoed as the published value so the only row that could differ is np's.
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", subdomainPolicy: "quarantine", np: "none");

        var dto = await InspectAsync(db, domainId, DnsPublishing(record));

        var np = dto.Comparison.Single(c => c.Field == "np");
        Assert.Equal(RecordComparisonStatus.Inherited, np.Status);
        Assert.Null(np.Published);
        Assert.Equal("none", np.Observed);
        Assert.Contains($"inherit {inheritedFrom}", np.Note);
        Assert.DoesNotContain(dto.Comparison, c => c.Status == RecordComparisonStatus.Differs);
    }

    [Fact]
    public async Task Inspect_NpPublishedButNotSent_IsNotReported()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", np: null);

        var dto = await InspectAsync(db, domainId, DnsPublishing("v=DMARC1; p=reject; np=reject; rua=mailto:d@acme.example"));

        var np = dto.Comparison.Single(c => c.Field == "np");
        Assert.Equal(RecordComparisonStatus.NotReported, np.Status);
        Assert.Equal("reject", np.Published);
        Assert.Contains("sent no np value", np.Note);
    }

    /// <summary>A row from before PR 1 has no np because nothing recorded it, not because the reporter omitted it.</summary>
    [Fact]
    public async Task Inspect_NpOnAReportStoredBeforeTheColumn_SaysNotRecorded()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", np: null, reportFormat: null);

        var dto = await InspectAsync(db, domainId, DnsPublishing("v=DMARC1; p=reject; np=reject; rua=mailto:d@acme.example"));

        var np = dto.Comparison.Single(c => c.Field == "np");
        Assert.Equal(RecordComparisonStatus.NotReported, np.Status);
        Assert.Contains("stored before np was recorded", np.Note);
    }

    [Theory]
    [InlineData("reject", RecordComparisonStatus.Match)]
    [InlineData("quarantine", RecordComparisonStatus.Differs)]
    public async Task Inspect_NpPublishedAndSent_IsCompared(string observedNp, string expected)
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", np: observedNp);

        var dto = await InspectAsync(db, domainId, DnsPublishing("v=DMARC1; p=reject; np=reject; rua=mailto:d@acme.example"));

        var np = dto.Comparison.Single(c => c.Field == "np");
        Assert.Equal(expected, np.Status);
        Assert.Equal("reject", np.Published);
        Assert.Equal(observedNp, np.Observed);
    }

    /// <summary>t has a fixed default, so an unpublished tag compares as n — unlike sp and np, nothing is derived.</summary>
    [Fact]
    public async Task Inspect_TestingNotPublished_ComparesAsN()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", testing: false, reportFormat: DmarcReportFormats.Rfc9990);

        var dto = await InspectAsync(db, domainId, DnsPublishing("v=DMARC1; p=reject; rua=mailto:d@acme.example"));

        var t = dto.Comparison.Single(c => c.Field == "t");
        Assert.Equal(RecordComparisonStatus.Match, t.Status);
        Assert.Equal("n", t.Published);
        Assert.Equal("n", t.Observed);
    }

    /// <summary>No RFC 7489 report carries a testing flag, so the note says which RFC the report follows.</summary>
    [Theory]
    [InlineData(DmarcReportFormats.Rfc7489, "RFC 7489")]
    [InlineData(DmarcReportFormats.Rfc9990, "sent no testing value")]
    [InlineData(null, "stored before testing was recorded")]
    public async Task Inspect_TestingNotSent_IsNotReportedWithTheReason(string? reportFormat, string expectedNote)
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", testing: null, reportFormat: reportFormat);

        var dto = await InspectAsync(db, domainId, DnsPublishing("v=DMARC1; p=reject; t=y; rua=mailto:d@acme.example"));

        var t = dto.Comparison.Single(c => c.Field == "t");
        Assert.Equal(RecordComparisonStatus.NotReported, t.Status);
        Assert.Equal("y", t.Published);
        Assert.Null(t.Observed);
        Assert.Contains(expectedNote, t.Note);
    }

    [Theory]
    [InlineData(true, RecordComparisonStatus.Match)]
    [InlineData(false, RecordComparisonStatus.Differs)]
    public async Task Inspect_TestingPublishedAndSent_IsCompared(bool observedTesting, string expected)
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", testing: observedTesting, reportFormat: DmarcReportFormats.Rfc9990);

        var dto = await InspectAsync(db, domainId, DnsPublishing("v=DMARC1; p=reject; t=y; rua=mailto:d@acme.example"));

        var t = dto.Comparison.Single(c => c.Field == "t");
        Assert.Equal(expected, t.Status);
        Assert.Equal("y", t.Published);
        Assert.Equal(observedTesting ? "y" : "n", t.Observed);
    }

    [Fact]
    public void ParseDmarc_ExplicitlyWeakSp_IsRaisedAsAnIssue()
    {
        var weak = RecordInspectionService.ParseDmarc(["v=DMARC1; p=reject; sp=none; rua=mailto:d@acme.example"]);
        Assert.Contains(weak.Issues, x => x.Contains("sp=none is weaker than p=reject"));

        // Absent sp inherits p, so there is nothing to warn about.
        var inherited = RecordInspectionService.ParseDmarc(["v=DMARC1; p=reject; rua=mailto:d@acme.example"]);
        Assert.DoesNotContain(inherited.Issues, x => x.Contains("weaker than"));

        // Stronger subdomain policy is unusual but not a gap.
        var stronger = RecordInspectionService.ParseDmarc(["v=DMARC1; p=none; sp=reject; rua=mailto:d@acme.example"]);
        Assert.DoesNotContain(stronger.Issues, x => x.Contains("weaker than"));
    }

    // --- External-destination authorization (RFC 9990 §4) ---

    [Fact]
    public async Task Inspect_ExternalRuaAuthorized_ReportsAuthorized()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject");

        var dns = new FakeDns(new()
        {
            ["_dmarc.acme.example"] = ["v=DMARC1; p=reject; rua=mailto:dmarc@agency.example"],
            ["acme.example"] = ["v=spf1 -all"],
            ["acme.example._report._dmarc.agency.example"] = ["v=DMARC1"],
        });

        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Admin(), dns, new DmarcPolicyResolver(dns))
            .InspectAsync(domainId, CancellationToken.None);

        var dest = Assert.Single(dto!.ExternalDestinations);
        Assert.Equal("agency.example", dest.Destination);
        Assert.Equal(ExternalDestinationAuthStatus.Authorized, dest.Status);
    }

    [Fact]
    public async Task Inspect_ExternalRuaNotAuthorized_ReportsNotAuthorized()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject");

        var dns = new FakeDns(new()
        {
            ["_dmarc.acme.example"] = ["v=DMARC1; p=reject; rua=mailto:dmarc@agency.example"],
            ["acme.example"] = ["v=spf1 -all"],
            // Empty (not omitted): NXDOMAIN/no TXT records — agency.example never opted in.
            ["acme.example._report._dmarc.agency.example"] = Array.Empty<string>(),
        });

        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Admin(), dns, new DmarcPolicyResolver(dns))
            .InspectAsync(domainId, CancellationToken.None);

        var dest = Assert.Single(dto!.ExternalDestinations);
        Assert.Equal("agency.example", dest.Destination);
        Assert.Equal(ExternalDestinationAuthStatus.NotAuthorized, dest.Status);
    }

    [Fact]
    public async Task Inspect_ExternalDestinationLookupFails_ReportsLookupFailed()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject");

        var dns = new FakeDns(new()
        {
            ["_dmarc.acme.example"] = ["v=DMARC1; p=reject; rua=mailto:dmarc@agency.example"],
            ["acme.example"] = ["v=spf1 -all"],
            // Explicit null (not omitted): simulates a SERVFAIL/timeout on this lookup.
            ["acme.example._report._dmarc.agency.example"] = null,
        });

        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Admin(), dns, new DmarcPolicyResolver(dns))
            .InspectAsync(domainId, CancellationToken.None);

        var dest = Assert.Single(dto!.ExternalDestinations);
        Assert.Equal(ExternalDestinationAuthStatus.LookupFailed, dest.Status);
    }

    [Fact]
    public async Task Inspect_RuaOnOwnDomain_NoExternalCheck()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject");

        var dns = new FakeDns(new()
        {
            ["_dmarc.acme.example"] = ["v=DMARC1; p=reject; rua=mailto:dmarc@acme.example"],
            ["acme.example"] = ["v=spf1 -all"],
        });

        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Admin(), dns, new DmarcPolicyResolver(dns))
            .InspectAsync(domainId, CancellationToken.None);

        Assert.Empty(dto!.ExternalDestinations);
    }

    [Fact]
    public async Task Inspect_InheritedRecord_SkipsExternalCheck()
    {
        await using var db = NewDb();
        // mail.acme.example (3 labels) so the tree walk has a real ancestor to find —
        // a 2-label domain like acme.example has none (Ancestors stops below 2 remaining
        // labels), so this needs its own seed rather than the default "acme.example".
        var domainId = await SeedDomainWithReportAsync(db, policy: "reject", domainName: "mail.acme.example");

        // mail.acme.example itself publishes no record; the parent org domain does, with
        // an external rua. The inherited-vs-own-record judgement call is deliberately not
        // made here — see CheckExternalDestinationsAsync's doc comment.
        var dns = new FakeDns(new()
        {
            // Empty (not omitted): NXDOMAIN/no record here, so the resolver walks up —
            // omitting the key would read as a lookup failure and stop the walk instead.
            ["_dmarc.mail.acme.example"] = Array.Empty<string>(),
            ["_dmarc.acme.example"] = ["v=DMARC1; p=reject; rua=mailto:dmarc@agency.example"],
            ["mail.acme.example"] = ["v=spf1 -all"],
        });

        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Admin(), dns, new DmarcPolicyResolver(dns))
            .InspectAsync(domainId, CancellationToken.None);

        Assert.Equal(RecordLookupStatus.Inherited, dto!.Dmarc.Status);
        Assert.Empty(dto.ExternalDestinations);
    }

    [Fact]
    public async Task Inspect_CrossTenant_ReturnsNull()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, "none");

        var dns = new FakeDns([]);
        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Viewer(Guid.NewGuid()), dns, new DmarcPolicyResolver(dns))
            .InspectAsync(domainId, CancellationToken.None);

        Assert.Null(dto);
    }
}
