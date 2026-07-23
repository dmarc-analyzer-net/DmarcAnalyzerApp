using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class TenantScopingTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static Client NewClient(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Name = slug,
        Slug = slug,
        Timezone = "UTC",
        RetentionMonths = 27,
        IsActive = true,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static Domain NewDomain(Guid clientId, string name) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Name = name,
        IsActive = true,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static async Task<(Client granted, Client other, Domain grantedDomain, Domain otherDomain)> SeedAsync(DmarcAnalyzerDbContext db)
    {
        var granted = NewClient("granted");
        var other = NewClient("other");
        var grantedDomain = NewDomain(granted.Id, "granted.example");
        var otherDomain = NewDomain(other.Id, "other.example");
        db.AddRange(granted, other, grantedDomain, otherDomain);
        await db.SaveChangesAsync();
        return (granted, other, grantedDomain, otherDomain);
    }

    [Fact]
    public async Task ClientList_ForViewer_ReturnsOnlyGrantedClients()
    {
        await using var db = NewDb();
        var (granted, other, _, _) = await SeedAsync(db);

        var viewerService = new ClientService(db, TestCurrentUserContext.Viewer(granted.Id));
        var viewerClients = await viewerService.ListAsync(CancellationToken.None);

        Assert.Single(viewerClients);
        Assert.Equal(granted.Id, viewerClients[0].Id);

        var analystService = new ClientService(db, TestCurrentUserContext.Analyst());
        var analystClients = await analystService.ListAsync(CancellationToken.None);
        Assert.Equal(2, analystClients.Count);

        Assert.Null(await viewerService.GetAsync(other.Id, CancellationToken.None));
        Assert.NotNull(await viewerService.GetAsync(granted.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DomainList_ForViewer_ReturnsOnlyGrantedClientDomains()
    {
        await using var db = NewDb();
        var (granted, _, grantedDomain, otherDomain) = await SeedAsync(db);

        var service = new DomainService(db, TestCurrentUserContext.Viewer(granted.Id));

        var domains = await service.ListAsync(null, CancellationToken.None);
        Assert.Single(domains);
        Assert.Equal(grantedDomain.Id, domains[0].Id);

        Assert.Null(await service.GetAsync(otherDomain.Id, CancellationToken.None));
        Assert.NotNull(await service.GetAsync(grantedDomain.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DomainDrilldown_ForViewer_HidesCrossTenantDomains()
    {
        await using var db = NewDb();
        var (granted, _, grantedDomain, otherDomain) = await SeedAsync(db);

        var service = new AnalyticsQueryService(db, TestCurrentUserContext.Viewer(granted.Id));

        Assert.Null(await service.GetDomainDrilldownAsync(otherDomain.Id, 30, CancellationToken.None));
        Assert.NotNull(await service.GetDomainDrilldownAsync(grantedDomain.Id, 30, CancellationToken.None));

        Assert.Null(await service.GetSourceDetailAsync(otherDomain.Id, "1.2.3.4", 30, CancellationToken.None));
    }

    [Fact]
    public async Task DomainAnalyticsList_ForViewer_ReturnsOnlyGrantedDomains()
    {
        await using var db = NewDb();
        var (granted, _, grantedDomain, _) = await SeedAsync(db);

        var service = new AnalyticsQueryService(db, TestCurrentUserContext.Viewer(granted.Id));
        var rows = await service.ListDomainAnalyticsAsync(30, CancellationToken.None);

        Assert.Single(rows);
        Assert.Equal(grantedDomain.Id, rows[0].DomainId);
    }

    [Fact]
    public async Task Summary_ForViewer_OmitsMailboxes_AndScopesDomainCount()
    {
        await using var db = NewDb();
        var (granted, _, _, _) = await SeedAsync(db);

        var viewer = new AnalyticsQueryService(db, TestCurrentUserContext.Viewer(granted.Id));
        var viewerSummary = await viewer.GetSummaryAsync(30, CancellationToken.None);

        Assert.Null(viewerSummary.Mailboxes);
        Assert.Equal(1, viewerSummary.Totals.Domains);

        var admin = new AnalyticsQueryService(db, TestCurrentUserContext.Admin());
        var adminSummary = await admin.GetSummaryAsync(30, CancellationToken.None);

        Assert.NotNull(adminSummary.Mailboxes);
        Assert.Equal(2, adminSummary.Totals.Domains);
    }
}
