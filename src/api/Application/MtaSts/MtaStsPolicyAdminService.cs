using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.MtaSts;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.MtaSts;

public interface IMtaStsPolicyAdminService
{
    /// <summary>The domain's hosted policy (or null Policy when none) — tenancy-scoped; null for unknown/cross-tenant ids.</summary>
    Task<MtaStsPolicyResponse?> GetAsync(Guid domainId, CancellationToken ct);

    Task<ServiceResult<MtaStsPolicyUpsertResult>> UpsertAsync(
        Guid domainId, UpsertMtaStsPolicyRequest request, CancellationToken ct);

    /// <summary>Removes the hosted policy — "we no longer host this". 404 when there is none.</summary>
    Task<ServiceResult<MtaStsPolicyResponse>> DeleteAsync(Guid domainId, CancellationToken ct);

    Task<ServiceResult<MtaStsPolicyBulkApplyResponse>> BulkApplyAsync(
        Guid clientId, BulkApplyMtaStsPolicyRequest request, CancellationToken ct);
}

/// <summary>
/// Manages hosted MTA-STS policies. The load-bearing rule is the id bump: the
/// policy id changes exactly when the rendered policy content changes. Senders
/// only refetch when the id moves, so an unchanged id on changed content
/// strands them on the stale policy until max_age expires, and a bumped id on
/// unchanged content makes every sender refetch for nothing.
/// </summary>
public sealed class MtaStsPolicyAdminService(
    DmarcAnalyzerDbContext db,
    ICurrentUserContext currentUser,
    IMemoryCache cache,
    IOptions<MtaStsOptions> options) : IMtaStsPolicyAdminService
{
    private static readonly string[] ValidModes = ["enforce", "testing", "none"];

    public async Task<MtaStsPolicyResponse?> GetAsync(Guid domainId, CancellationToken ct)
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

        var policy = await db.MtaStsPolicies
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.DomainId == domainId, ct);

        return ToResponse(domain.Id, domain.Name, domain.ClientId, policy);
    }

    public async Task<ServiceResult<MtaStsPolicyUpsertResult>> UpsertAsync(
        Guid domainId, UpsertMtaStsPolicyRequest request, CancellationToken ct)
    {
        var domain = await db.Domains
            .AsNoTracking()
            .Where(x => x.Id == domainId)
            .Select(x => new { x.Id, x.Name, x.ClientId })
            .SingleOrDefaultAsync(ct);

        if (domain is null)
        {
            return ServiceResult<MtaStsPolicyUpsertResult>.Failure("not found", 404);
        }

        var validation = Validate(request.Mode, request.MaxAgeSeconds, request.MxPatterns);
        if (validation.Error is not null)
        {
            return ServiceResult<MtaStsPolicyUpsertResult>.Failure(validation.Error, 400);
        }

        var policy = await db.MtaStsPolicies.SingleOrDefaultAsync(p => p.DomainId == domainId, ct);
        var (outcome, previousPolicyId) = ApplyToRow(ref policy, domainId, request.Enabled, validation, DateTime.UtcNow);

        if (outcome == MtaStsPolicyOutcome.Created)
        {
            db.MtaStsPolicies.Add(policy!);
        }

        if (outcome != MtaStsPolicyOutcome.Unchanged)
        {
            await db.SaveChangesAsync(ct);
            cache.Remove(MtaStsPolicyHostService.CacheKey(domain.Name));
        }

        return ServiceResult<MtaStsPolicyUpsertResult>.Success(new MtaStsPolicyUpsertResult(
            ToResponse(domain.Id, domain.Name, domain.ClientId, policy), outcome, previousPolicyId));
    }

    public async Task<ServiceResult<MtaStsPolicyResponse>> DeleteAsync(Guid domainId, CancellationToken ct)
    {
        var domain = await db.Domains
            .AsNoTracking()
            .Where(x => x.Id == domainId)
            .Select(x => new { x.Id, x.Name, x.ClientId })
            .SingleOrDefaultAsync(ct);

        if (domain is null)
        {
            return ServiceResult<MtaStsPolicyResponse>.Failure("not found", 404);
        }

        var policy = await db.MtaStsPolicies.SingleOrDefaultAsync(p => p.DomainId == domainId, ct);
        if (policy is null)
        {
            return ServiceResult<MtaStsPolicyResponse>.Failure("no hosted policy for this domain", 404);
        }

        db.MtaStsPolicies.Remove(policy);
        await db.SaveChangesAsync(ct);
        cache.Remove(MtaStsPolicyHostService.CacheKey(domain.Name));

        return ServiceResult<MtaStsPolicyResponse>.Success(
            ToResponse(domain.Id, domain.Name, domain.ClientId, null));
    }

    public async Task<ServiceResult<MtaStsPolicyBulkApplyResponse>> BulkApplyAsync(
        Guid clientId, BulkApplyMtaStsPolicyRequest request, CancellationToken ct)
    {
        var clientExists = await db.Clients.AnyAsync(x => x.Id == clientId, ct);
        if (!clientExists)
        {
            return ServiceResult<MtaStsPolicyBulkApplyResponse>.Failure("client not found", 404);
        }

        var validation = Validate(request.Mode, request.MaxAgeSeconds, request.MxPatterns);
        if (validation.Error is not null)
        {
            return ServiceResult<MtaStsPolicyBulkApplyResponse>.Failure(validation.Error, 400);
        }

        List<(Guid Id, string Name)> targets;
        if (request.AllDomains)
        {
            targets = (await db.Domains
                .AsNoTracking()
                .Where(x => x.ClientId == clientId && x.IsActive)
                .OrderBy(x => x.Name)
                .Select(x => new { x.Id, x.Name })
                .ToListAsync(ct))
                .Select(x => (x.Id, x.Name)).ToList();

            if (targets.Count == 0)
            {
                return ServiceResult<MtaStsPolicyBulkApplyResponse>.Failure(
                    "the client has no active domains", 400);
            }
        }
        else
        {
            if (request.DomainIds.Length == 0)
            {
                return ServiceResult<MtaStsPolicyBulkApplyResponse>.Failure(
                    "domainIds is required unless allDomains is true", 400);
            }

            var ids = request.DomainIds.Distinct().ToArray();
            var rows = await db.Domains
                .AsNoTracking()
                .Where(x => ids.Contains(x.Id))
                .Select(x => new { x.Id, x.Name, x.ClientId, x.IsActive })
                .ToListAsync(ct);

            // Every id must be this client's — a cross-client id in the list is
            // a mistake worth stopping, not a row worth skipping silently.
            var foreign = ids.Except(rows.Where(r => r.ClientId == clientId).Select(r => r.Id)).ToArray();
            if (foreign.Length > 0)
            {
                return ServiceResult<MtaStsPolicyBulkApplyResponse>.Failure(
                    $"{foreign.Length} domain id(s) do not belong to this client", 400);
            }

            var inactive = rows.Where(r => !r.IsActive).Select(r => r.Name).ToArray();
            if (inactive.Length > 0)
            {
                return ServiceResult<MtaStsPolicyBulkApplyResponse>.Failure(
                    $"inactive domain(s): {string.Join(", ", inactive)}", 400);
            }

            targets = rows.OrderBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => (r.Id, r.Name)).ToList();
        }

        var policies = await db.MtaStsPolicies
            .Where(p => targets.Select(t => t.Id).Contains(p.DomainId))
            .ToDictionaryAsync(p => p.DomainId, ct);

        var now = DateTime.UtcNow;
        var results = new List<MtaStsPolicyApplyOutcomeDto>(targets.Count);
        var anyPersisted = false;

        foreach (var (id, name) in targets)
        {
            policies.TryGetValue(id, out var policy);
            var (outcome, _) = ApplyToRow(ref policy, id, request.Enabled, validation, now);

            if (outcome == MtaStsPolicyOutcome.Created)
            {
                db.MtaStsPolicies.Add(policy!);
            }

            anyPersisted |= outcome != MtaStsPolicyOutcome.Unchanged;
            results.Add(new MtaStsPolicyApplyOutcomeDto(
                id, name, outcome, policy!.PolicyId,
                $"_mta-sts.{name}", $"v=STSv1; id={policy.PolicyId}"));
        }

        if (anyPersisted)
        {
            await db.SaveChangesAsync(ct);
            foreach (var (_, name) in targets)
            {
                cache.Remove(MtaStsPolicyHostService.CacheKey(name));
            }
        }

        return ServiceResult<MtaStsPolicyBulkApplyResponse>.Success(
            new MtaStsPolicyBulkApplyResponse(results));
    }

    private sealed record ValidatedPolicy(string Mode, int MaxAgeSeconds, IReadOnlyList<string> Patterns, string? Error)
    {
        public static ValidatedPolicy Failure(string error) => new("", 0, [], error);
    }

    private static ValidatedPolicy Validate(string mode, int maxAgeSeconds, string[] mxPatterns)
    {
        var normalizedMode = mode.Trim().ToLowerInvariant();
        if (!ValidModes.Contains(normalizedMode))
        {
            return ValidatedPolicy.Failure("mode must be enforce, testing or none");
        }

        // Floor 3600 rather than a day: testing-mode operators legitimately want
        // short max_age so mistakes age out fast. The cap is the RFC's.
        if (maxAgeSeconds is < 3600 or > 31_557_600)
        {
            return ValidatedPolicy.Failure("maxAgeSeconds must be between 3600 and 31557600");
        }

        var patterns = new List<string>();
        foreach (var raw in mxPatterns)
        {
            var pattern = raw.Trim().TrimEnd('.').ToLowerInvariant();
            if (pattern.Length == 0)
            {
                continue;
            }

            if (!MtaStsCheckService.IsValidMxPattern(pattern))
            {
                return ValidatedPolicy.Failure(
                    $"\"{raw}\" is not a valid mx pattern — use a hostname like mx1.example.com, " +
                    "optionally with a leading *. for one wildcard label");
            }

            if (!patterns.Contains(pattern))
            {
                patterns.Add(pattern);
            }
        }

        if (patterns.Count == 0 && normalizedMode != "none")
        {
            return ValidatedPolicy.Failure("at least one mx pattern is required unless mode is none");
        }

        if (patterns.Count > 32 || string.Join('\n', patterns).Length > 2000)
        {
            return ValidatedPolicy.Failure("too many mx patterns — at most 32, 2000 characters joined");
        }

        return new ValidatedPolicy(normalizedMode, maxAgeSeconds, patterns, null);
    }

    /// <summary>
    /// Folds a validated request into a (possibly new) row. Shared by the single
    /// and bulk paths so their semantics cannot drift: the id bumps iff the
    /// rendered content differs, an Enabled flip alone persists without a bump,
    /// and a fully identical request touches nothing — not even UpdatedAtUtc.
    /// </summary>
    private static (string Outcome, string? PreviousPolicyId) ApplyToRow(
        ref MtaStsPolicy? policy, Guid domainId, bool enabled, ValidatedPolicy validated, DateTime nowUtc)
    {
        var joinedPatterns = string.Join('\n', validated.Patterns);

        if (policy is null)
        {
            policy = new MtaStsPolicy
            {
                DomainId = domainId,
                Enabled = enabled,
                Mode = validated.Mode,
                MaxAgeSeconds = validated.MaxAgeSeconds,
                MxPatterns = joinedPatterns,
                PolicyId = NewPolicyId(nowUtc, previous: null),
                ModeChangedAtUtc = nowUtc,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            };
            return (MtaStsPolicyOutcome.Created, null);
        }

        var contentChanged =
            MtaStsCheckService.RenderPolicyFile(validated.Mode, validated.MaxAgeSeconds, validated.Patterns)
            != MtaStsCheckService.RenderPolicyFile(
                policy.Mode, policy.MaxAgeSeconds, MtaStsPolicyHostService.SplitPatterns(policy.MxPatterns));

        if (!contentChanged && policy.Enabled == enabled)
        {
            return (MtaStsPolicyOutcome.Unchanged, null);
        }

        string? previousPolicyId = null;
        if (contentChanged)
        {
            previousPolicyId = policy.PolicyId;
            policy.PolicyId = NewPolicyId(nowUtc, policy.PolicyId);

            if (!string.Equals(policy.Mode, validated.Mode, StringComparison.Ordinal))
            {
                policy.ModeChangedAtUtc = nowUtc;
            }

            policy.Mode = validated.Mode;
            policy.MaxAgeSeconds = validated.MaxAgeSeconds;
            policy.MxPatterns = joinedPatterns;
        }

        policy.Enabled = enabled;
        policy.UpdatedAtUtc = nowUtc;
        return (MtaStsPolicyOutcome.Updated, previousPolicyId);
    }

    /// <summary>
    /// yyyyMMddHHmmss UTC — sortable, obviously a timestamp to a human reading
    /// DNS, and within RFC 8461's 1–32 alphanumeric grammar. A same-second
    /// double save formats one second later instead of reusing the id, because
    /// an unchanged id on changed content is the failure mode that matters.
    /// </summary>
    public static string NewPolicyId(DateTime nowUtc, string? previous)
    {
        var id = nowUtc.ToString("yyyyMMddHHmmss");
        return string.Equals(id, previous, StringComparison.Ordinal)
            ? nowUtc.AddSeconds(1).ToString("yyyyMMddHHmmss")
            : id;
    }

    private MtaStsPolicyResponse ToResponse(Guid domainId, string domainName, Guid clientId, MtaStsPolicy? policy)
    {
        var policyHost = options.Value.PolicyHost.Trim().TrimEnd('.');
        return new MtaStsPolicyResponse(
            domainId,
            domainName,
            clientId,
            policy is null ? null : ToDto(policy, domainName),
            $"mta-sts.{domainName}",
            policyHost.Length == 0 ? null : policyHost.ToLowerInvariant());
    }

    private static MtaStsPolicyDto ToDto(MtaStsPolicy policy, string domainName) => new(
        policy.Id,
        policy.DomainId,
        policy.Enabled,
        policy.Mode,
        policy.MaxAgeSeconds,
        MtaStsPolicyHostService.SplitPatterns(policy.MxPatterns),
        policy.PolicyId,
        $"_mta-sts.{domainName}",
        $"v=STSv1; id={policy.PolicyId}",
        $"https://mta-sts.{domainName}/.well-known/mta-sts.txt",
        policy.ModeChangedAtUtc,
        policy.CreatedAtUtc,
        policy.UpdatedAtUtc);
}
