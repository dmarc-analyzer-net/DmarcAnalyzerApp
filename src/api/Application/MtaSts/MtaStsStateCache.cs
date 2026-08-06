using System.Text.Json;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.MtaSts;

/// <summary>How a check pass went, for the worker log.</summary>
public sealed record MtaStsRefreshResult(int Checked, int Changed, int Failed);

public interface IMtaStsStateCache
{
    /// <summary>
    /// Checks every active domain and stores the outcome. Never-checked domains
    /// first, then least-recently-checked, so an interrupted pass makes progress
    /// next time. Network checks run concurrently (bounded by
    /// <see cref="MtaStsOptions.MaxConcurrentChecks"/>); writes are sequential.
    /// </summary>
    Task<MtaStsRefreshResult> RefreshAllAsync(CancellationToken ct);

    /// <summary>Stores one check result (the on-demand recheck path) and saves.</summary>
    Task<MtaStsState> ApplyAsync(Guid domainId, MtaStsCheckResult result, CancellationToken ct);
}

/// <summary>
/// Persists MTA-STS check results on <c>mta_sts_state</c>, one row per domain.
///
/// Same doctrine as <see cref="Analytics.DnsPolicyCache"/>: a failed lookup keeps
/// the last known values rather than blanking them, nothing here bumps the
/// domain's UpdatedAtUtc, and no audit events are written — a background check is
/// not an operator action. Change *notification* is the alert evaluator's job,
/// reading the columns this writes.
/// </summary>
public sealed class MtaStsStateCache(
    DmarcAnalyzerDbContext db,
    IMtaStsCheckService checkService,
    IOptions<MtaStsOptions> options,
    ILogger<MtaStsStateCache> logger) : IMtaStsStateCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MtaStsRefreshResult> RefreshAllAsync(CancellationToken ct)
    {
        var domains = await db.Domains
            .Where(x => x.IsActive)
            .Select(x => new
            {
                x.Id,
                x.Name,
                LastCheckedAtUtc = db.MtaStsStates
                    .Where(s => s.DomainId == x.Id)
                    .Select(s => (DateTime?)s.LastCheckedAtUtc)
                    .FirstOrDefault(),
            })
            .OrderBy(x => x.LastCheckedAtUtc == null ? 0 : 1)
            .ThenBy(x => x.LastCheckedAtUtc)
            .ToListAsync(ct);

        if (domains.Count == 0)
        {
            return new MtaStsRefreshResult(0, 0, 0);
        }

        // Network phase, concurrent and DbContext-free. A crashed check maps to a
        // lookup-failed result so one bad domain cannot take down the pass.
        var gate = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentChecks));
        var checks = await Task.WhenAll(domains.Select(async domain =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return (domain.Id, Result: await checkService.CheckAsync(domain.Name, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MTA-STS check crashed for {Domain}", domain.Name);
                return (domain.Id, Result: CrashedCheck());
            }
            finally
            {
                gate.Release();
            }
        }));

        // Write phase, sequential on the tracked context.
        var domainIds = domains.Select(d => d.Id).ToList();
        var states = await db.MtaStsStates
            .Where(s => domainIds.Contains(s.DomainId))
            .ToDictionaryAsync(s => s.DomainId, ct);

        var changed = 0;
        var failed = 0;
        var now = DateTime.UtcNow;

        foreach (var (domainId, result) in checks)
        {
            if (!states.TryGetValue(domainId, out var state))
            {
                state = new MtaStsState { DomainId = domainId };
                db.MtaStsStates.Add(state);
            }

            if (Apply(state, result, now))
            {
                changed++;
            }

            if (result.Record.Status == MtaStsRecordStatus.LookupFailed)
            {
                failed++;
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "MTA-STS refresh: checked {Checked}, changed {Changed}, lookup failures {Failed}",
            domains.Count, changed, failed);

        return new MtaStsRefreshResult(domains.Count, changed, failed);
    }

    public async Task<MtaStsState> ApplyAsync(Guid domainId, MtaStsCheckResult result, CancellationToken ct)
    {
        var state = await db.MtaStsStates.SingleOrDefaultAsync(s => s.DomainId == domainId, ct);
        if (state is null)
        {
            state = new MtaStsState { DomainId = domainId };
            db.MtaStsStates.Add(state);
        }

        Apply(state, result, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        return state;
    }

    /// <summary>
    /// Folds one check result into the row. Public static so the keep-last-known
    /// rules are unit-testable without DNS, HTTP or a database.
    ///
    /// The rules, per TXT outcome:
    /// lookup_failed keeps everything (a SERVFAIL must not make an enforce-mode
    /// domain read as unprotected); missing is definitive and clears the policy
    /// fields — including the id-change history, because a withdrawn policy is
    /// not a change; invalid keeps the last-known policy/fetch fields (nothing
    /// further was checked) but records the broken record; found stores fresh
    /// values, keeping the last-known body when only the fetch failed.
    /// </summary>
    public static bool Apply(MtaStsState state, MtaStsCheckResult check, DateTime nowUtc)
    {
        var before = MaterialSnapshot(state);
        var record = check.Record;

        switch (record.Status)
        {
            case MtaStsRecordStatus.LookupFailed:
                state.DnsRecordStatus = MtaStsRecordStatus.LookupFailed;
                break;

            case MtaStsRecordStatus.Missing:
                state.DnsRecordStatus = MtaStsRecordStatus.Missing;
                state.RawRecord = null;
                state.PolicyId = null;
                state.PreviousPolicyId = null;
                state.PolicyIdChangedAtUtc = null;
                state.FetchStatus = null;
                state.FetchDetail = null;
                state.PolicyValid = null;
                state.Mode = null;
                state.MaxAgeSeconds = null;
                state.PolicyBody = null;
                state.MxLookupStatus = null;
                state.MxHostsJson = null;
                state.UnmatchedMxHostsJson = null;
                // LastFetchOkAtUtc survives on purpose — see the entity doc.
                break;

            case MtaStsRecordStatus.Invalid:
                state.DnsRecordStatus = MtaStsRecordStatus.Invalid;
                state.RawRecord = record.Raw;
                break;

            case MtaStsRecordStatus.Found:
                state.DnsRecordStatus = MtaStsRecordStatus.Found;
                state.RawRecord = record.Raw;

                if (record.Id is not null)
                {
                    if (state.PolicyId is not null
                        && !string.Equals(state.PolicyId, record.Id, StringComparison.Ordinal))
                    {
                        state.PreviousPolicyId = state.PolicyId;
                        state.PolicyIdChangedAtUtc = nowUtc;
                    }

                    state.PolicyId = record.Id;
                }

                if (check.Fetch is { } fetch)
                {
                    state.FetchStatus = fetch.Status;
                    state.FetchDetail = Truncate(fetch.Detail, 1000);

                    if (fetch.Status == MtaStsFetchStatus.Ok)
                    {
                        state.LastFetchOkAtUtc = nowUtc;
                        state.PolicyBody = fetch.Body;
                        state.PolicyValid = check.Policy?.Valid;
                        state.Mode = check.Policy?.Mode;
                        state.MaxAgeSeconds = check.Policy?.MaxAgeSeconds;
                    }
                    // A failed fetch keeps the last-known body/mode — the policy
                    // most likely still is what it was; the failure itself is the news.
                }

                if (check.MxLookupStatus is { } mxStatus)
                {
                    state.MxLookupStatus = mxStatus;
                    if (mxStatus != MtaStsMxStatus.LookupFailed)
                    {
                        state.MxHostsJson = SerializeMxHosts(check);
                        state.UnmatchedMxHostsJson = check.UnmatchedMxHosts is null
                            ? null
                            : JsonSerializer.Serialize(check.UnmatchedMxHosts, Json);
                    }
                }

                break;
        }

        state.IssuesJson = check.Issues.Count == 0 ? null : JsonSerializer.Serialize(check.Issues, Json);

        // Always advanced, even when nothing moved: "we verified this" is what it is for.
        state.LastCheckedAtUtc = nowUtc;

        var changed = MaterialSnapshot(state) != before;
        if (changed)
        {
            state.LastChangedAtUtc = nowUtc;
        }

        return changed;
    }

    private static string? SerializeMxHosts(MtaStsCheckResult check)
    {
        if (check.MxHosts is null)
        {
            return null;
        }

        var unmatched = check.UnmatchedMxHosts is null
            ? null
            : new HashSet<string>(check.UnmatchedMxHosts, StringComparer.OrdinalIgnoreCase);

        var hosts = check.MxHosts.Select(h => new MtaStsMxHostDto(
            h.Host.Length == 0 ? "." : h.Host, // RFC 7505 null MX, legible again
            h.Preference,
            h.Host.Length == 0 || unmatched is null ? null : !unmatched.Contains(h.Host)));

        return JsonSerializer.Serialize(hosts, Json);
    }

    /// <summary>
    /// The fields whose movement counts as "something changed" — everything except
    /// the two always-advancing timestamps (LastCheckedAtUtc, LastFetchOkAtUtc).
    /// </summary>
    private static string MaterialSnapshot(MtaStsState s) => string.Join('\u001f',
        s.DnsRecordStatus, s.RawRecord, s.PolicyId, s.PreviousPolicyId,
        s.FetchStatus, s.FetchDetail, s.PolicyValid?.ToString(), s.Mode, s.MaxAgeSeconds?.ToString(),
        s.PolicyBody, s.MxLookupStatus, s.MxHostsJson, s.UnmatchedMxHostsJson, s.IssuesJson);

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];

    private static MtaStsCheckResult CrashedCheck()
    {
        var issues = new[] { "The check failed unexpectedly — see the worker log." };
        return new MtaStsCheckResult(
            new MtaStsRecordParseResult(MtaStsRecordStatus.LookupFailed, null, null, issues),
            null, null, null, null, null, issues);
    }
}
