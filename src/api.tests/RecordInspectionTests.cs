using DmarcAnalyzer.Api.Application.Analytics;
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
        public Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct)
            => Task.FromResult(answers.GetValueOrDefault(name));
    }

    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static async Task<Guid> SeedDomainWithReportAsync(
        DmarcAnalyzerDbContext db, string policy, string? subdomainPolicy = null, bool spReported = true)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC",
            RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var domain = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = "acme.example", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.AddRange(client, domain, new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domain.Id, MailboxSourceId = Guid.NewGuid(),
            OrganizationName = "google.com", ReportId = "r1",
            RangeBeginUtc = DateTime.UtcNow.AddDays(-2), RangeEndUtc = DateTime.UtcNow.AddDays(-1),
            RecordCount = 0, IngestedAtUtc = DateTime.UtcNow,
            PublishedPolicy = policy,
            SubdomainPolicy = spReported ? subdomainPolicy ?? policy : null,
            PublishedPct = 100,
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
