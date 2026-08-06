using System.Text.Json;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.MtaSts;

public interface IMtaStsInspectionService
{
    /// <summary>
    /// The persisted MTA-STS state for a domain — database only, no network, so
    /// the panel renders instantly. Null for unknown or cross-tenant ids.
    /// </summary>
    Task<MtaStsStateDto?> GetAsync(Guid domainId, CancellationToken ct);

    /// <summary>
    /// Runs a live check now, persists it (keep-last-known rules apply) and
    /// returns the updated state. Null for unknown or cross-tenant ids.
    /// </summary>
    Task<MtaStsStateDto?> RecheckAsync(Guid domainId, CancellationToken ct);
}

public sealed class MtaStsInspectionService(
    DmarcAnalyzerDbContext db,
    ICurrentUserContext currentUser,
    IMtaStsCheckService checkService,
    IMtaStsStateCache stateCache,
    IMtaStsReadinessService readiness) : IMtaStsInspectionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MtaStsStateDto?> GetAsync(Guid domainId, CancellationToken ct)
    {
        var domain = await ResolveAccessibleDomainAsync(domainId, ct);
        if (domain is null)
        {
            return null;
        }

        var state = await db.MtaStsStates
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.DomainId == domainId, ct);

        return ToDto(domain.Value.Id, domain.Value.Name, state,
            await readiness.GetForDomainAsync(domainId, ct));
    }

    public async Task<MtaStsStateDto?> RecheckAsync(Guid domainId, CancellationToken ct)
    {
        var domain = await ResolveAccessibleDomainAsync(domainId, ct);
        if (domain is null)
        {
            return null;
        }

        var result = await checkService.CheckAsync(domain.Value.Name, ct);
        var state = await stateCache.ApplyAsync(domain.Value.Id, result, ct);
        return ToDto(domain.Value.Id, domain.Value.Name, state,
            await readiness.GetForDomainAsync(domainId, ct));
    }

    private async Task<(Guid Id, string Name)?> ResolveAccessibleDomainAsync(Guid domainId, CancellationToken ct)
    {
        var domain = await db.Domains
            .AsNoTracking()
            .Where(x => x.Id == domainId)
            .Select(x => new { x.Id, x.Name, x.ClientId })
            .SingleOrDefaultAsync(ct);

        // Cross-tenant ids read as not-found to avoid an existence oracle.
        if (domain is null || !currentUser.CanAccessClient(domain.ClientId))
        {
            return null;
        }

        return (domain.Id, domain.Name);
    }

    private static MtaStsStateDto ToDto(
        Guid domainId, string name, MtaStsState? state, MtaStsReadinessDto? readiness)
    {
        if (state is null)
        {
            return new MtaStsStateDto(
                domainId, name, Checked: false,
                null, null, null, null, null, null, null, null, null, null, null, null,
                [], null, [], [], null, null, readiness);
        }

        // mx patterns re-parse from the stored body rather than being stored
        // twice; the parser is pure and the body is the source of truth.
        var mxPatterns = state.PolicyBody is null
            ? []
            : MtaStsCheckService.ParsePolicy(state.PolicyBody).MxPatterns;

        return new MtaStsStateDto(
            domainId, name, Checked: true,
            state.DnsRecordStatus,
            state.RawRecord,
            state.PolicyId,
            state.PreviousPolicyId,
            state.PolicyIdChangedAtUtc,
            state.FetchStatus,
            state.FetchDetail,
            state.LastFetchOkAtUtc,
            state.PolicyValid,
            state.Mode,
            state.MaxAgeSeconds,
            state.PolicyBody,
            mxPatterns,
            state.MxLookupStatus,
            Deserialize<List<MtaStsMxHostDto>>(state.MxHostsJson) ?? [],
            Deserialize<List<string>>(state.IssuesJson) ?? [],
            state.LastCheckedAtUtc,
            state.LastChangedAtUtc,
            readiness);
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null; // a malformed stored blob renders as empty, not as a 500
        }
    }
}
