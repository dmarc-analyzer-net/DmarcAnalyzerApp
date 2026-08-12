using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// The domain resolver, which both ingestors call before they open a transaction.
/// <para>
/// It inserts with <c>ON CONFLICT (Name) DO NOTHING</c> and then re-queries rather than
/// trusting the id it generated, because a concurrent insert may have won the conflict.
/// That reasoning is written in a comment beside the code and, until now, verified by
/// nothing — and it is not reachable from the InMemory provider, which executes neither
/// the conflict clause nor the unique index it depends on.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DomainIngestResolverTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid ClientId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();

        await using var db = postgres.CreateContext();
        db.Clients.Add(new Client
        {
            Id = ClientId, Name = "Acme", Slug = "acme", IsActive = true,
            RetentionMonths = 12, Timezone = "UTC",
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AnUnknownDomainIsCreatedUnderTheSourcesDefaultClient()
    {
        var id = await ResolveAsync("acme.test");

        await using var db = postgres.CreateContext();
        var domain = await db.Domains.SingleAsync();
        Assert.Equal(id, domain.Id);
        Assert.Equal(ClientId, domain.ClientId);
    }

    [Fact]
    public async Task ResolvingTwiceReturnsTheSameDomainRatherThanASecondRow()
    {
        var first = await ResolveAsync("acme.test");
        var second = await ResolveAsync("acme.test");

        Assert.Equal(first, second);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.Domains.CountAsync());
    }

    /// <summary>
    /// The race the code's own comment describes. Ten resolvers go for the same new
    /// domain at once: exactly one insert can win the unique index, the other nine get
    /// <c>DO NOTHING</c>, and every one of them must come back with the winner's id.
    /// A resolver that returned the id it generated locally would hand nine callers a
    /// foreign key pointing at a row that does not exist.
    /// </summary>
    [Fact]
    public async Task ConcurrentResolversAllAgreeOnTheWinningRow()
    {
        var ids = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => ResolveAsync("contended.test")));

        Assert.Single(ids.Distinct());

        await using var db = postgres.CreateContext();
        var domain = await db.Domains.SingleAsync(x => x.Name == "contended.test");
        Assert.Equal(domain.Id, ids[0]);
    }

    [Fact]
    public async Task DomainsAreGloballyUniqueSoASecondClientCannotClaimOne()
    {
        var otherClient = Guid.Parse("66666666-6666-6666-6666-666666666666");
        await using (var db = postgres.CreateContext())
        {
            db.Clients.Add(new Client
            {
                Id = otherClient, Name = "Other", Slug = "other", IsActive = true,
                RetentionMonths = 12, Timezone = "UTC",
            });
            await db.SaveChangesAsync();
        }

        var first = await ResolveAsync("shared.test", ClientId);
        var second = await ResolveAsync("shared.test", otherClient);

        // Global uniqueness is what makes tenancy derivable through the domain, so the
        // second client resolves to the first client's row rather than getting its own —
        // and is told whose it is, which is what lets a caller refuse it.
        Assert.Equal(first, second);
        Assert.Equal(ClientId, (await ResolveFullAsync("shared.test", otherClient)).OwnerClientId);

        await using var check = postgres.CreateContext();
        Assert.Equal(ClientId, (await check.Domains.SingleAsync(x => x.Name == "shared.test")).ClientId);
    }

    private async Task<Guid> ResolveAsync(string domain, Guid? clientId = null)
        => (await ResolveFullAsync(domain, clientId)).DomainId;

    private async Task<ResolvedDomain> ResolveFullAsync(string domain, Guid? clientId = null)
    {
        await using var db = postgres.CreateContext();
        return await new DomainIngestResolver(db)
            .ResolveOrCreateAsync(clientId ?? ClientId, domain, CancellationToken.None);
    }
}
