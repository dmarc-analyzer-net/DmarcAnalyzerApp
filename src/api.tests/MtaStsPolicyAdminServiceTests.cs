using DmarcAnalyzer.Api.Application.MtaSts;
using DmarcAnalyzer.Api.Contracts.MtaSts;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Hosted-policy management, above all the id-bump rule: the id changes exactly
/// when the rendered content changes. Senders only refetch on an id move, so
/// either direction of drift strands them — on a stale policy, or on pointless
/// refetches.
/// </summary>
public sealed class MtaStsPolicyAdminServiceTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static MtaStsPolicyAdminService Service(
        DmarcAnalyzerDbContext db, IMemoryCache? cache = null, string policyHost = "sts.agency.example")
        => new(db, TestCurrentUserContext.Admin(), cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new MtaStsOptions { PolicyHost = policyHost }));

    private static async Task<(Client Client, Domain Domain)> SeedAsync(
        DmarcAnalyzerDbContext db, string slug = "acme", string domainName = "acme.example")
    {
        var client = new Client { Id = Guid.NewGuid(), Name = slug, Slug = slug, Timezone = "UTC" };
        var domain = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = domainName, IsActive = true };
        db.AddRange(client, domain);
        await db.SaveChangesAsync();
        return (client, domain);
    }

    private static UpsertMtaStsPolicyRequest Request(
        string mode = "testing", int maxAge = 86400, string[]? patterns = null, bool enabled = true)
        => new() { Enabled = enabled, Mode = mode, MaxAgeSeconds = maxAge, MxPatterns = patterns ?? ["mx1.acme.example"] };

    [Fact]
    public async Task Create_ReturnsThePublishInstructions()
    {
        await using var db = NewDb();
        var (_, domain) = await SeedAsync(db);

        var result = await Service(db).UpsertAsync(domain.Id, Request(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var upsert = result.Value!;
        Assert.Equal(MtaStsPolicyOutcome.Created, upsert.Outcome);
        var policy = upsert.Response.Policy!;
        Assert.Equal("_mta-sts.acme.example", policy.TxtRecordName);
        Assert.Equal($"v=STSv1; id={policy.PolicyId}", policy.TxtRecordValue);
        Assert.Equal("https://mta-sts.acme.example/.well-known/mta-sts.txt", policy.PolicyUrl);
        Assert.Equal("mta-sts.acme.example", upsert.Response.CnameRecordName);
        Assert.Equal("sts.agency.example", upsert.Response.CnameTarget);
        Assert.Matches("^[0-9]{14}$", policy.PolicyId);
    }

    [Fact]
    public async Task UnconfiguredPolicyHost_ReadsAsNullCnameTarget()
    {
        await using var db = NewDb();
        var (_, domain) = await SeedAsync(db);

        var response = await Service(db, policyHost: "").GetAsync(domain.Id, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response!.CnameTarget);
        Assert.Null(response.Policy); // none created yet — a 200 with null policy, not a 404
    }

    [Theory]
    [InlineData("enforced", 86400, new[] { "mx1.a.example" }, "mode")]
    [InlineData("testing", 3599, new[] { "mx1.a.example" }, "maxAgeSeconds")]
    [InlineData("testing", 31557601, new[] { "mx1.a.example" }, "maxAgeSeconds")]
    [InlineData("testing", 86400, new[] { "not_a_host" }, "not a valid mx pattern")]
    [InlineData("testing", 86400, new string[0], "at least one mx pattern")]
    [InlineData("enforce", 86400, new[] { "" }, "at least one mx pattern")]
    public async Task Validation_RejectsBadRequests(string mode, int maxAge, string[] patterns, string errorFragment)
    {
        await using var db = NewDb();
        var (_, domain) = await SeedAsync(db);

        var result = await Service(db).UpsertAsync(
            domain.Id, Request(mode, maxAge, patterns), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
        Assert.Contains(errorFragment, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModeNone_NeedsNoPatterns_AndKeepsStoredOnes()
    {
        await using var db = NewDb();
        var (_, domain) = await SeedAsync(db);
        var service = Service(db);
        await service.UpsertAsync(domain.Id, Request(), CancellationToken.None);

        // Switching to none with the patterns still in the request keeps them —
        // switching back to testing later must not have lost the list.
        var result = await service.UpsertAsync(domain.Id, Request(mode: "none"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["mx1.acme.example"], result.Value!.Response.Policy!.MxPatterns);
    }

    [Fact]
    public async Task IdBumps_OnContentChange_AndOnlyThen()
    {
        await using var db = NewDb();
        var (_, domain) = await SeedAsync(db);
        var service = Service(db);

        var created = (await service.UpsertAsync(domain.Id, Request(), CancellationToken.None)).Value!;
        var originalId = created.Response.Policy!.PolicyId;

        // Identical request: no bump, nothing persisted.
        var identical = (await service.UpsertAsync(domain.Id, Request(), CancellationToken.None)).Value!;
        Assert.Equal(MtaStsPolicyOutcome.Unchanged, identical.Outcome);
        Assert.Equal(originalId, identical.Response.Policy!.PolicyId);

        // Enabled flip alone: persisted, still no bump — content didn't change.
        var disabled = (await service.UpsertAsync(domain.Id, Request(enabled: false), CancellationToken.None)).Value!;
        Assert.Equal(MtaStsPolicyOutcome.Updated, disabled.Outcome);
        Assert.Equal(originalId, disabled.Response.Policy!.PolicyId);
        Assert.Null(disabled.PreviousPolicyId);

        // Content change: bump, previous id reported for the audit trail.
        var changed = (await service.UpsertAsync(
            domain.Id, Request(mode: "enforce", enabled: false), CancellationToken.None)).Value!;
        Assert.Equal(MtaStsPolicyOutcome.Updated, changed.Outcome);
        Assert.NotEqual(originalId, changed.Response.Policy!.PolicyId);
        Assert.Equal(originalId, changed.PreviousPolicyId);
    }

    [Fact]
    public void SameSecondDoubleSave_ProducesDistinctIds()
    {
        var now = new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);
        var first = MtaStsPolicyAdminService.NewPolicyId(now, previous: null);
        var second = MtaStsPolicyAdminService.NewPolicyId(now, previous: first);

        Assert.NotEqual(first, second);
        Assert.Equal("20260806120000", first);
        Assert.Equal("20260806120001", second);
    }

    [Fact]
    public async Task ModeChange_MovesTheModeClock_OtherEditsDoNot()
    {
        await using var db = NewDb();
        var (_, domain) = await SeedAsync(db);
        var service = Service(db);
        await service.UpsertAsync(domain.Id, Request(), CancellationToken.None);
        var afterCreate = (await db.MtaStsPolicies.SingleAsync()).ModeChangedAtUtc;

        await Task.Delay(20);
        await service.UpsertAsync(domain.Id, Request(maxAge: 172800), CancellationToken.None);
        Assert.Equal(afterCreate, (await db.MtaStsPolicies.SingleAsync()).ModeChangedAtUtc);

        await service.UpsertAsync(domain.Id, Request(mode: "enforce", maxAge: 172800), CancellationToken.None);
        Assert.True((await db.MtaStsPolicies.SingleAsync()).ModeChangedAtUtc > afterCreate);
    }

    [Fact]
    public async Task Get_IsTenancyScoped_WritePathsAre404ForUnknownDomains()
    {
        await using var db = NewDb();
        var (granted, grantedDomain) = await SeedAsync(db, "granted", "granted.example");
        var (_, otherDomain) = await SeedAsync(db, "other", "other.example");

        var viewerService = new MtaStsPolicyAdminService(
            db, TestCurrentUserContext.Viewer(granted.Id),
            new MemoryCache(new MemoryCacheOptions()), Options.Create(new MtaStsOptions()));

        Assert.NotNull(await viewerService.GetAsync(grantedDomain.Id, CancellationToken.None));
        Assert.Null(await viewerService.GetAsync(otherDomain.Id, CancellationToken.None));

        var missing = await Service(db).UpsertAsync(Guid.NewGuid(), Request(), CancellationToken.None);
        Assert.Equal(404, missing.StatusCode);
        Assert.Equal(404, (await Service(db).DeleteAsync(grantedDomain.Id, CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task BulkApply_ReportsPerDomainOutcomes_AndBumpsOnlyWhatChanged()
    {
        await using var db = NewDb();
        var (client, first) = await SeedAsync(db, "acme", "first.example");
        var second = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "second.example", IsActive = true };
        var third = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "third.example", IsActive = true };
        db.AddRange(second, third);
        await db.SaveChangesAsync();

        var service = Service(db);
        // first already hosts the exact shape the bulk apply will send.
        await service.UpsertAsync(first.Id, Request(), CancellationToken.None);
        // second hosts a different shape.
        await service.UpsertAsync(second.Id, Request(mode: "none", patterns: []), CancellationToken.None);

        var result = await service.BulkApplyAsync(client.Id, new BulkApplyMtaStsPolicyRequest
        {
            AllDomains = true,
            Mode = "testing",
            MaxAgeSeconds = 86400,
            MxPatterns = ["mx1.acme.example"],
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var byName = result.Value!.Results.ToDictionary(r => r.DomainName, r => r.Outcome);
        Assert.Equal(MtaStsPolicyOutcome.Unchanged, byName["first.example"]);
        Assert.Equal(MtaStsPolicyOutcome.Updated, byName["second.example"]);
        Assert.Equal(MtaStsPolicyOutcome.Created, byName["third.example"]);
        Assert.All(result.Value!.Results, r => Assert.StartsWith("v=STSv1; id=", r.TxtRecordValue));
        Assert.Equal(3, await db.MtaStsPolicies.CountAsync());
    }

    [Fact]
    public async Task BulkApply_RejectsCrossClientAndInactiveDomains()
    {
        await using var db = NewDb();
        var (client, mine) = await SeedAsync(db, "acme", "mine.example");
        var (_, foreign) = await SeedAsync(db, "other", "foreign.example");
        var inactive = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "inactive.example", IsActive = false };
        db.Add(inactive);
        await db.SaveChangesAsync();
        var service = Service(db);

        var crossClient = await service.BulkApplyAsync(client.Id, new BulkApplyMtaStsPolicyRequest
        {
            DomainIds = [mine.Id, foreign.Id], Mode = "testing", MaxAgeSeconds = 86400, MxPatterns = ["mx.a.example"],
        }, CancellationToken.None);
        Assert.Equal(400, crossClient.StatusCode);
        Assert.Contains("do not belong", crossClient.Error);

        var inactiveResult = await service.BulkApplyAsync(client.Id, new BulkApplyMtaStsPolicyRequest
        {
            DomainIds = [inactive.Id], Mode = "testing", MaxAgeSeconds = 86400, MxPatterns = ["mx.a.example"],
        }, CancellationToken.None);
        Assert.Equal(400, inactiveResult.StatusCode);
        Assert.Contains("inactive", inactiveResult.Error);

        Assert.Equal(0, await db.MtaStsPolicies.CountAsync()); // nothing partially applied
    }

    [Fact]
    public async Task Delete_RemovesTheRow()
    {
        await using var db = NewDb();
        var (_, domain) = await SeedAsync(db);
        var service = Service(db);
        await service.UpsertAsync(domain.Id, Request(), CancellationToken.None);

        var result = await service.DeleteAsync(domain.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, await db.MtaStsPolicies.CountAsync());
    }
}
