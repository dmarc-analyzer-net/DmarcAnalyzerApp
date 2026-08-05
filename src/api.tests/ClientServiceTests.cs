using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Contracts.Clients;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ClientServiceTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static ClientService NewService(DmarcAnalyzerDbContext db)
        => new(db, TestCurrentUserContext.Admin());

    [Fact]
    public async Task EnsureDefault_OnEmptyInstall_CreatesTheDefaultClient()
    {
        await using var db = NewDb();
        var service = NewService(db);

        var created = await service.EnsureDefaultAsync(CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal(ClientService.DefaultClientName, created!.Name);
        Assert.Equal(ClientService.DefaultClientSlug, created.Slug);
        Assert.True(created.IsActive);
        Assert.Equal(27, created.RetentionMonths);
        Assert.Equal("UTC", created.Timezone);
        Assert.Single(await db.Clients.ToListAsync());
    }

    [Fact]
    public async Task EnsureDefault_IsIdempotent()
    {
        await using var db = NewDb();
        var service = NewService(db);

        var first = await service.EnsureDefaultAsync(CancellationToken.None);
        var second = await service.EnsureDefaultAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(await db.Clients.ToListAsync());
    }

    /// <summary>
    /// The restore path brings its own clients, and import never deletes — so creating a
    /// catch-all here would strand an empty client that no endpoint can remove.
    /// </summary>
    [Fact]
    public async Task EnsureDefault_WhenAnyClientExists_CreatesNothing()
    {
        await using var db = NewDb();
        db.Clients.Add(new Client
        {
            Id = Guid.NewGuid(),
            Name = "Acme Inc",
            Slug = "acme-inc",
            Timezone = "UTC",
            RetentionMonths = 27,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var result = await NewService(db).EnsureDefaultAsync(CancellationToken.None);

        Assert.Null(result);
        var only = Assert.Single(await db.Clients.ToListAsync());
        Assert.Equal("acme-inc", only.Slug);
    }

    [Fact]
    public async Task Update_LeavesTheSlugAlone()
    {
        await using var db = NewDb();
        var service = NewService(db);
        var created = await service.EnsureDefaultAsync(CancellationToken.None);

        var result = await service.UpdateAsync(
            created!.Id,
            new UpdateClientRequest { Name = "Renamed", RetentionMonths = 12 },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Renamed", result.Value!.Name);
        Assert.Equal(12, result.Value.RetentionMonths);
        Assert.Equal(ClientService.DefaultClientSlug, result.Value.Slug);
    }
}
