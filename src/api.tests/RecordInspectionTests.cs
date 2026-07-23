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
        Assert.Empty(dto.Issues);
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

    private static async Task<Guid> SeedDomainWithReportAsync(DmarcAnalyzerDbContext db, string policy)
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
            PublishedPolicy = policy, SubdomainPolicy = policy, PublishedPct = 100,
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

        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Admin(), dns)
            .InspectAsync(domainId, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(RecordLookupStatus.Found, dto!.Dmarc.Status);
        Assert.Equal("quarantine", dto.Dmarc.Policy);
        Assert.NotNull(dto.Observed);
        Assert.Equal("none", dto.Observed!.Policy);

        var p = dto.Comparison.Single(c => c.Field == "p");
        Assert.False(p.Match);
        var pct = dto.Comparison.Single(c => c.Field == "pct");
        Assert.True(pct.Match);
    }

    [Fact]
    public async Task Inspect_CrossTenant_ReturnsNull()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithReportAsync(db, "none");

        var dns = new FakeDns([]);
        var dto = await new RecordInspectionService(db, TestCurrentUserContext.Viewer(Guid.NewGuid()), dns)
            .InspectAsync(domainId, CancellationToken.None);

        Assert.Null(dto);
    }
}
