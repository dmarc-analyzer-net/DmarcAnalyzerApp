using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.MtaSts;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The keep-last-known rules: a transient lookup or fetch failure must not make
/// an enforce-mode domain read as unprotected, while a definitive "no record"
/// clears everything. Guarded here because the worker pass exercises them
/// against real DNS, where the failure cases are exactly the ones that never
/// occur in a demo.
/// </summary>
public sealed class MtaStsStateCacheTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MtaStsCheckResult Found(
        string id = "a1",
        string mode = "enforce",
        string fetchStatus = MtaStsFetchStatus.Ok,
        string? fetchDetail = null,
        string? mxLookupStatus = MtaStsMxStatus.Found,
        string[]? unmatched = null)
    {
        var record = new MtaStsRecordParseResult(
            MtaStsRecordStatus.Found, $"v=STSv1; id={id}", id, []);
        var body = $"version: STSv1\nmode: {mode}\nmx: mx1.acme.example\nmax_age: 604800\n";
        var ok = fetchStatus == MtaStsFetchStatus.Ok;
        var fetch = new MtaStsPolicyFetchResult(
            fetchStatus, ok ? body : null, ok ? 200 : null, fetchDetail, ok ? "text/plain" : null);
        var policy = ok ? MtaStsCheckService.ParsePolicy(body) : null;
        var hosts = mxLookupStatus == MtaStsMxStatus.Found
            ? new[] { new MxHost(10, "mx1.acme.example") }
            : null;

        return new MtaStsCheckResult(record, fetch, policy, mxLookupStatus, hosts, unmatched ?? [], []);
    }

    private static MtaStsCheckResult LookupFailed()
    {
        var issues = new[] { "DNS lookup failed — could not check the record." };
        return new MtaStsCheckResult(
            new MtaStsRecordParseResult(MtaStsRecordStatus.LookupFailed, null, null, issues),
            null, null, null, null, null, issues);
    }

    private static MtaStsCheckResult Missing()
        => new(new MtaStsRecordParseResult(MtaStsRecordStatus.Missing, null, null, []),
            null, null, null, null, null, []);

    [Fact]
    public void FirstFoundCheck_StoresEverything_WithoutAnIdChange()
    {
        var state = new MtaStsState { DomainId = Guid.NewGuid() };

        var changed = MtaStsStateCache.Apply(state, Found(), T0);

        Assert.True(changed);
        Assert.Equal(MtaStsRecordStatus.Found, state.DnsRecordStatus);
        Assert.Equal("a1", state.PolicyId);
        Assert.Null(state.PreviousPolicyId);          // first observation is not a change
        Assert.Null(state.PolicyIdChangedAtUtc);
        Assert.Equal(MtaStsFetchStatus.Ok, state.FetchStatus);
        Assert.Equal(T0, state.LastFetchOkAtUtc);
        Assert.True(state.PolicyValid);
        Assert.Equal("enforce", state.Mode);
        Assert.Equal(604800, state.MaxAgeSeconds);
        Assert.NotNull(state.PolicyBody);
        Assert.Equal("[]", state.UnmatchedMxHostsJson);
        Assert.Equal(T0, state.LastCheckedAtUtc);
        Assert.Equal(T0, state.LastChangedAtUtc);
    }

    [Fact]
    public void LookupFailure_KeepsLastKnown_AndStillAdvancesCheckedAt()
    {
        var state = new MtaStsState { DomainId = Guid.NewGuid() };
        MtaStsStateCache.Apply(state, Found(), T0);

        var changed = MtaStsStateCache.Apply(state, LookupFailed(), T0.AddHours(6));

        Assert.True(changed); // the status itself moved
        Assert.Equal(MtaStsRecordStatus.LookupFailed, state.DnsRecordStatus);
        Assert.Equal("a1", state.PolicyId);           // kept
        Assert.Equal("enforce", state.Mode);          // kept
        Assert.NotNull(state.PolicyBody);             // kept
        Assert.Equal(MtaStsFetchStatus.Ok, state.FetchStatus); // kept
        Assert.Equal(T0.AddHours(6), state.LastCheckedAtUtc);

        // A second identical failure is not a further change.
        var changedAgain = MtaStsStateCache.Apply(state, LookupFailed(), T0.AddHours(12));
        Assert.False(changedAgain);
        Assert.Equal(T0.AddHours(6), state.LastChangedAtUtc);
        Assert.Equal(T0.AddHours(12), state.LastCheckedAtUtc);
    }

    [Fact]
    public void Missing_IsDefinitive_AndClearsThePolicyFields()
    {
        var state = new MtaStsState { DomainId = Guid.NewGuid() };
        MtaStsStateCache.Apply(state, Found(), T0);

        MtaStsStateCache.Apply(state, Missing(), T0.AddHours(6));

        Assert.Equal(MtaStsRecordStatus.Missing, state.DnsRecordStatus);
        Assert.Null(state.PolicyId);
        Assert.Null(state.PreviousPolicyId);          // a withdrawn policy is not an id change
        Assert.Null(state.Mode);
        Assert.Null(state.PolicyBody);
        Assert.Null(state.FetchStatus);
        Assert.Equal(T0, state.LastFetchOkAtUtc);     // never cleared — "was ever reachable"
    }

    [Fact]
    public void FetchFailure_KeepsTheLastKnownPolicy_ButRecordsTheFailure()
    {
        var state = new MtaStsState { DomainId = Guid.NewGuid() };
        MtaStsStateCache.Apply(state, Found(), T0);

        MtaStsStateCache.Apply(state,
            Found(fetchStatus: MtaStsFetchStatus.HttpError, fetchDetail: "HTTP 503.", mxLookupStatus: null, unmatched: null),
            T0.AddHours(6));

        Assert.Equal(MtaStsFetchStatus.HttpError, state.FetchStatus);
        Assert.Equal("HTTP 503.", state.FetchDetail);
        Assert.Equal("enforce", state.Mode);          // kept
        Assert.NotNull(state.PolicyBody);             // kept
        Assert.True(state.PolicyValid);               // kept
        Assert.Equal(T0, state.LastFetchOkAtUtc);     // not advanced by a failure
    }

    [Fact]
    public void IdChange_RecordsThePreviousId_Once()
    {
        var state = new MtaStsState { DomainId = Guid.NewGuid() };
        MtaStsStateCache.Apply(state, Found(id: "a1"), T0);

        MtaStsStateCache.Apply(state, Found(id: "b2"), T0.AddHours(6));

        Assert.Equal("b2", state.PolicyId);
        Assert.Equal("a1", state.PreviousPolicyId);
        Assert.Equal(T0.AddHours(6), state.PolicyIdChangedAtUtc);

        // Re-observing the same id later must not refresh the change timestamp.
        MtaStsStateCache.Apply(state, Found(id: "b2"), T0.AddHours(12));
        Assert.Equal(T0.AddHours(6), state.PolicyIdChangedAtUtc);
        Assert.Equal("a1", state.PreviousPolicyId);
    }

    [Fact]
    public void UnchangedCheck_IsNotAMaterialChange()
    {
        var state = new MtaStsState { DomainId = Guid.NewGuid() };
        MtaStsStateCache.Apply(state, Found(), T0);

        var changed = MtaStsStateCache.Apply(state, Found(), T0.AddHours(6));

        Assert.False(changed);
        Assert.Equal(T0, state.LastChangedAtUtc);
        Assert.Equal(T0.AddHours(6), state.LastCheckedAtUtc);
        Assert.Equal(T0.AddHours(6), state.LastFetchOkAtUtc); // ok fetches keep proving reachability
    }

    [Fact]
    public void InvalidRecord_KeepsLastKnownPolicy_AndRecordsTheBrokenRecord()
    {
        var state = new MtaStsState { DomainId = Guid.NewGuid() };
        MtaStsStateCache.Apply(state, Found(), T0);

        var invalid = new MtaStsCheckResult(
            new MtaStsRecordParseResult(MtaStsRecordStatus.Invalid, "v=STSv1", null,
                ["The record has no id= tag — senders cannot tell when the policy changes and treat the record as invalid."]),
            null, null, null, null, null,
            ["The record has no id= tag — senders cannot tell when the policy changes and treat the record as invalid."]);
        MtaStsStateCache.Apply(state, invalid, T0.AddHours(6));

        Assert.Equal(MtaStsRecordStatus.Invalid, state.DnsRecordStatus);
        Assert.Equal("v=STSv1", state.RawRecord);
        Assert.Equal("a1", state.PolicyId);           // kept — nothing further was checked
        Assert.NotNull(state.PolicyBody);             // kept
    }

    [Fact]
    public async Task RefreshAll_CreatesRowsForActiveDomains_AndSkipsInactive()
    {
        await using var db = new DmarcAnalyzerDbContext(
            new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

        var client = new Client { Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC" };
        var active = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "acme.example", IsActive = true };
        var inactive = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "old.example", IsActive = false };
        db.AddRange(client, active, inactive);
        await db.SaveChangesAsync();

        var txt = new TestDnsTxtResolver().Publish("_mta-sts.acme.example", "v=STSv1; id=a1");
        var mx = new TestDnsMxResolver().Publish("acme.example", new MxHost(10, "mx1.acme.example"));
        var fetcher = new TestMtaStsPolicyFetcher()
            .Serve("acme.example", "version: STSv1\nmode: testing\nmx: mx1.acme.example\nmax_age: 86400\n");
        var cache = new MtaStsStateCache(
            db,
            new MtaStsCheckService(txt, mx, fetcher),
            Options.Create(new MtaStsOptions()),
            NullLogger<MtaStsStateCache>.Instance);

        var result = await cache.RefreshAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Checked);
        Assert.Equal(1, result.Changed);
        Assert.Equal(0, result.Failed);
        var state = Assert.Single(await db.MtaStsStates.ToListAsync());
        Assert.Equal(active.Id, state.DomainId);
        Assert.Equal(MtaStsRecordStatus.Found, state.DnsRecordStatus);
        Assert.Equal("testing", state.Mode);
    }
}
