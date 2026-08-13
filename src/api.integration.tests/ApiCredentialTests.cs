using DmarcAnalyzer.Api.Application.ApiCredentials;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// Credential issue, verify and revoke against a real database — the unique index on
/// <c>TokenId</c> and the lookup that depends on it are not things the InMemory provider
/// can be trusted about.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ApiCredentialTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid ClientId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid SourceId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();

        await using var db = postgres.CreateContext();
        db.Clients.Add(new Client
        {
            Id = ClientId, Name = "Acme", Slug = "acme", IsActive = true,
            RetentionMonths = 12, Timezone = "UTC",
        });
        db.ReportSources.Add(new ReportSource
        {
            Id = SourceId, Name = "Mail gateway", Protocol = "imap", Host = "imap.example.test",
            Port = 993, UseTls = true, Username = "rua@acme.test", PasswordEncrypted = "x",
            DefaultClientId = ClientId, IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task IssuingReturnsTheTokenOnceAndStoresOnlyItsHash()
    {
        var issued = await IssueAsync("mail-gateway-prod");

        Assert.StartsWith("dmarcanalyzer_", issued.Token);

        await using var db = postgres.CreateContext();
        var stored = await db.ApiCredentials.SingleAsync();

        // The secret half must not be recoverable from anything persisted.
        Assert.DoesNotContain(stored.TokenHash, issued.Token);
        Assert.DoesNotContain(issued.Token, stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length);          // SHA-256, hex
        Assert.Equal(MachineCredentialKinds.ReportIngest, stored.Kind);
        Assert.Equal(SourceId, stored.ReportSourceId);
    }

    [Fact]
    public async Task ThePresentedTokenVerifiesAgainstTheStoredHashAndAWrongOneDoesNot()
    {
        var issued = await IssueAsync("mail-gateway-prod");
        Assert.True(MachineToken.TryParse(issued.Token, out var tokenId, out var secret));

        await using var db = postgres.CreateContext();
        var stored = await db.ApiCredentials.SingleAsync(x => x.TokenId == tokenId);

        Assert.True(MachineToken.VerifySecret(secret, stored.TokenHash));
        Assert.False(MachineToken.VerifySecret(secret + "x", stored.TokenHash));
    }

    [Fact]
    public async Task TwoCredentialsForOneSourceCoexistSoRotationDoesNotNeedAFlagDay()
    {
        // The reason a credential is a row rather than a column on the source (ADR 0010):
        // the replacement has to work before the old one is switched off.
        var first = await IssueAsync("mail-gateway-2026");
        var second = await IssueAsync("mail-gateway-2027");

        await using var db = postgres.CreateContext();
        var live = await db.ApiCredentials.Where(x => x.RevokedAtUtc == null).CountAsync();

        Assert.Equal(2, live);
        Assert.NotEqual(first.Credential.TokenId, second.Credential.TokenId);
    }

    [Fact]
    public async Task RevokingIsATimestampNotADeleteAndIsIdempotent()
    {
        var issued = await IssueAsync("mail-gateway-prod");

        Assert.NotNull((await RevokeAsync(issued.Credential.Id)).RevokedAtUtc);
        var afterFirst = await StoredRevokedAtAsync();

        await RevokeAsync(issued.Credential.Id);
        var afterSecond = await StoredRevokedAtAsync();

        // Compared as stored, not as returned. PostgreSQL keeps timestamps to the
        // microsecond and .NET to the 100-nanosecond tick, so a value straight out of
        // DateTime.UtcNow is fractionally finer than the one that comes back — comparing
        // across that boundary fails on a difference that is not a behaviour.
        Assert.NotNull(afterFirst);
        Assert.Equal(afterFirst, afterSecond);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.ApiCredentials.CountAsync());
    }

    [Fact]
    public async Task ARevokedOrExpiredCredentialIsNotUsable()
    {
        var issued = await IssueAsync("mail-gateway-prod");
        await RevokeAsync(issued.Credential.Id);

        await using var db = postgres.CreateContext();
        var stored = await db.ApiCredentials.SingleAsync();

        Assert.False(stored.IsUsable(DateTime.UtcNow));

        stored.RevokedAtUtc = null;
        stored.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        Assert.False(stored.IsUsable(DateTime.UtcNow));

        stored.ExpiresAtUtc = DateTime.UtcNow.AddDays(1);
        Assert.True(stored.IsUsable(DateTime.UtcNow));
    }

    [Fact]
    public async Task IssuingForAMissingSourceIsRefused()
    {
        await using var db = postgres.CreateContext();
        var result = await new ApiCredentialService(db)
            .IssueAsync(Guid.NewGuid(), "orphan", null, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public async Task ExpiryInThePastIsRefusedRatherThanIssuingSomethingBornDead()
    {
        await using var db = postgres.CreateContext();
        var result = await new ApiCredentialService(db)
            .IssueAsync(SourceId, "stale", DateTime.UtcNow.AddMinutes(-1), null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public async Task TokenIdsAreUniqueAcrossManyIssues()
    {
        // The unique index is the lookup's correctness guarantee, so it is worth showing
        // that minting does not collide in practice rather than only in theory.
        for (var i = 0; i < 25; i++)
        {
            await IssueAsync($"cred-{i}");
        }

        await using var db = postgres.CreateContext();
        var ids = await db.ApiCredentials.Select(x => x.TokenId).ToListAsync();
        Assert.Equal(25, ids.Distinct().Count());
    }

    private async Task<DateTime?> StoredRevokedAtAsync()
    {
        await using var db = postgres.CreateContext();
        return (await db.ApiCredentials.AsNoTracking().SingleAsync()).RevokedAtUtc;
    }

    private async Task<IssuedApiCredentialDto> IssueAsync(string name)
    {
        await using var db = postgres.CreateContext();
        var result = await new ApiCredentialService(db)
            .IssueAsync(SourceId, name, null, null, CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private async Task<ApiCredentialDto> RevokeAsync(Guid id)
    {
        await using var db = postgres.CreateContext();
        var result = await new ApiCredentialService(db).RevokeAsync(id, CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Value!;
    }
}
