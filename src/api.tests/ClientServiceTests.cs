using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Contracts.Auth;
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

    private static Client NewClient(string slug) => new()
    {
        Id = Guid.NewGuid(),
        Name = slug,
        Slug = slug,
        Timezone = "UTC",
        RetentionMonths = 27,
        IsActive = true,
    };

    // --- bootstrap creates it ---

    [Fact]
    public async Task Register_BootstrappingTheFirstAdmin_CreatesTheDefaultClient()
    {
        await using var db = NewDb();

        var result = await new AuthService(db).RegisterAsync(
            new RegisterRequest
            {
                Email = "admin@agency.tld",
                Password = "correct-horse-battery",
                DisplayName = "Admin",
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var client = Assert.Single(await db.Clients.ToListAsync());
        Assert.Equal(DefaultClient.Name, client.Name);
        Assert.Equal(DefaultClient.Slug, client.Slug);
        Assert.True(client.IsActive);
        Assert.Equal(27, client.RetentionMonths);
        Assert.Equal("UTC", client.Timezone);
    }

    [Fact]
    public async Task EnsureAsync_IsIdempotent()
    {
        await using var db = NewDb();

        var first = await DefaultClient.EnsureAsync(db, CancellationToken.None);
        var second = await DefaultClient.EnsureAsync(db, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(await db.Clients.ToListAsync());
    }

    [Fact]
    public async Task EnsureAsync_WhenAnyClientExists_CreatesNothing()
    {
        await using var db = NewDb();
        db.Clients.Add(NewClient("acme-inc"));
        await db.SaveChangesAsync();

        Assert.Null(await DefaultClient.EnsureAsync(db, CancellationToken.None));
        var only = Assert.Single(await db.Clients.ToListAsync());
        Assert.Equal("acme-inc", only.Slug);
    }

    // --- the pristine-install rule a restore is gated on ---

    /// <summary>
    /// The point of the carve-out: the default client is created during bootstrap, so counting
    /// it would leave no install that could ever be restored into.
    /// </summary>
    [Fact]
    public async Task IsPristineInstall_WithOnlyTheDefaultClient_IsTrue()
    {
        await using var db = NewDb();
        await DefaultClient.EnsureAsync(db, CancellationToken.None);

        Assert.True(await DefaultClient.IsPristineInstallAsync(db, CancellationToken.None));
    }

    [Fact]
    public async Task IsPristineInstall_WithAClientOfYourOwn_IsFalse()
    {
        await using var db = NewDb();
        await DefaultClient.EnsureAsync(db, CancellationToken.None);
        db.Clients.Add(NewClient("acme-inc"));
        await db.SaveChangesAsync();

        Assert.False(await DefaultClient.IsPristineInstallAsync(db, CancellationToken.None));
    }

    [Fact]
    public async Task IsPristineInstall_WithADomain_IsFalse()
    {
        await using var db = NewDb();
        var client = await DefaultClient.EnsureAsync(db, CancellationToken.None);
        db.Domains.Add(new Domain
        {
            Id = Guid.NewGuid(),
            ClientId = client!.Id,
            Name = "example.tld",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        Assert.False(await DefaultClient.IsPristineInstallAsync(db, CancellationToken.None));
    }

    /// <summary>
    /// A report source can only be created against a client, and on a fresh install that is
    /// the default one — so without counting sources, an install with a configured mailbox and
    /// no domains yet would still read as pristine and let a restore union two installs.
    /// </summary>
    [Fact]
    public async Task IsPristineInstall_WithAReportSourceButNoDomains_IsFalse()
    {
        await using var db = NewDb();
        var client = await DefaultClient.EnsureAsync(db, CancellationToken.None);
        db.ReportSources.Add(new ReportSource
        {
            Id = Guid.NewGuid(),
            DefaultClientId = client!.Id,
            Name = "Reports",
            Host = "imap.example.tld",
            Port = 993,
            Username = "u",
            PasswordEncrypted = "x",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        Assert.Empty(await db.Domains.ToListAsync());
        Assert.False(await DefaultClient.IsPristineInstallAsync(db, CancellationToken.None));
    }

    // --- slug immutability ---

    [Fact]
    public async Task Update_LeavesTheSlugAlone()
    {
        await using var db = NewDb();
        var created = await DefaultClient.EnsureAsync(db, CancellationToken.None);

        var result = await NewService(db).UpdateAsync(
            created!.Id,
            new UpdateClientRequest { Name = "Renamed", RetentionMonths = 12 },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Renamed", result.Value!.Name);
        Assert.Equal(12, result.Value.RetentionMonths);
        Assert.Equal(DefaultClient.Slug, result.Value.Slug);
    }
}
