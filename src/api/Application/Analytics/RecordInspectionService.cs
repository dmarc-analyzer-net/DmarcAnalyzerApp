using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Analytics;

public interface IRecordInspectionService
{
    /// <summary>Live DNS DMARC/SPF records for the domain, compared against what reporters observed.</summary>
    Task<RecordInspectionDto?> InspectAsync(Guid domainId, CancellationToken ct);
}

public sealed class RecordInspectionService(
    DmarcAnalyzerDbContext db,
    ICurrentUserContext currentUser,
    IDnsTxtResolver dns) : IRecordInspectionService
{
    public async Task<RecordInspectionDto?> InspectAsync(Guid domainId, CancellationToken ct)
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

        var dmarcTask = dns.ResolveAsync($"_dmarc.{domain.Name}", ct);
        var spfTask = dns.ResolveAsync(domain.Name, ct);

        var observedRow = await db.DmarcReports
            .AsNoTracking()
            .Where(x => x.DomainId == domainId)
            .OrderByDescending(x => x.RangeEndUtc)
            .ThenByDescending(x => x.IngestedAtUtc)
            .Select(x => new
            {
                x.PublishedPolicy,
                x.SubdomainPolicy,
                x.PublishedPct,
                x.DkimAlignment,
                x.SpfAlignment,
                x.RangeEndUtc,
                x.OrganizationName,
            })
            .FirstOrDefaultAsync(ct);

        var dmarc = ParseDmarc(await dmarcTask);
        var spf = ParseSpf(await spfTask);

        var observed = observedRow is null
            ? null
            : new ObservedPolicyDto(
                observedRow.PublishedPolicy,
                observedRow.SubdomainPolicy,
                observedRow.PublishedPct,
                observedRow.DkimAlignment,
                observedRow.SpfAlignment,
                observedRow.RangeEndUtc,
                observedRow.OrganizationName);

        return new RecordInspectionDto(
            domain.Id,
            domain.Name,
            dmarc,
            spf,
            observed,
            Compare(dmarc, observed));
    }

    // --- DMARC (RFC 7489) ---

    public static DnsDmarcRecordDto ParseDmarc(IReadOnlyList<string>? txts)
    {
        if (txts is null)
        {
            return new DnsDmarcRecordDto(RecordLookupStatus.LookupFailed, null, null, null, null, null, null, null, null,
                ["DNS lookup failed — could not check the record."]);
        }

        var records = txts.Where(t => t.TrimStart().StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToList();
        if (records.Count == 0)
        {
            return new DnsDmarcRecordDto(RecordLookupStatus.Missing, null, null, null, null, null, null, null, null,
                ["No DMARC record published at _dmarc — mail receivers apply no policy."]);
        }

        var issues = new List<string>();
        if (records.Count > 1)
        {
            // RFC 7489 §6.6.3: multiple records mean DMARC processing is skipped entirely.
            issues.Add($"{records.Count} DMARC records published — receivers ignore all of them. Remove the extras.");
        }

        var raw = records[0];
        var tags = ParseTags(raw);

        tags.TryGetValue("p", out var policy);
        if (policy is null)
        {
            issues.Add("Record has no p= tag — it is not a valid DMARC policy.");
        }

        tags.TryGetValue("rua", out var rua);
        if (rua is null)
        {
            issues.Add("No rua= tag — you are not receiving aggregate reports.");
        }

        int? pct = null;
        if (tags.TryGetValue("pct", out var pctRaw))
        {
            if (int.TryParse(pctRaw, out var parsedPct) && parsedPct is >= 0 and <= 100)
            {
                pct = parsedPct;
            }
            else
            {
                issues.Add($"Invalid pct= value \"{pctRaw}\".");
            }
        }

        return new DnsDmarcRecordDto(
            RecordLookupStatus.Found,
            raw,
            policy,
            tags.GetValueOrDefault("sp"),
            pct,
            rua,
            tags.GetValueOrDefault("ruf"),
            tags.GetValueOrDefault("adkim"),
            tags.GetValueOrDefault("aspf"),
            issues);
    }

    private static Dictionary<string, string> ParseTags(string record)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in record.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq].Trim();
            // First occurrence wins, matching receiver behavior for duplicate tags.
            tags.TryAdd(key, part[(eq + 1)..].Trim());
        }

        return tags;
    }

    // --- SPF (RFC 7208) ---

    public static DnsSpfRecordDto ParseSpf(IReadOnlyList<string>? txts)
    {
        if (txts is null)
        {
            return new DnsSpfRecordDto(RecordLookupStatus.LookupFailed, null, 0, 0, null,
                ["DNS lookup failed — could not check the record."]);
        }

        var records = txts.Where(t => t.TrimStart().StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
        if (records.Count == 0)
        {
            return new DnsSpfRecordDto(RecordLookupStatus.Missing, null, 0, 0, null,
                ["No SPF record published — receivers cannot verify your sending servers."]);
        }

        var issues = new List<string>();
        if (records.Count > 1)
        {
            // RFC 7208 §3.2: more than one record is a permerror.
            issues.Add($"{records.Count} SPF records published — this is a permerror; merge them into one.");
        }

        var raw = records[0];
        var lookups = 0;
        string? allQualifier = null;

        foreach (var term in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1))
        {
            var mechanism = term.TrimStart('+', '-', '~', '?');
            if (mechanism.StartsWith("include:", StringComparison.OrdinalIgnoreCase) ||
                mechanism.StartsWith("redirect=", StringComparison.OrdinalIgnoreCase) ||
                mechanism.StartsWith("exists:", StringComparison.OrdinalIgnoreCase) ||
                mechanism is "a" or "mx" or "ptr" ||
                mechanism.StartsWith("a:", StringComparison.OrdinalIgnoreCase) ||
                mechanism.StartsWith("mx:", StringComparison.OrdinalIgnoreCase) ||
                mechanism.StartsWith("ptr:", StringComparison.OrdinalIgnoreCase))
            {
                lookups++;
            }

            if (mechanism.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                allQualifier = term.Length == 3 ? "+" : term[..1];
            }
        }

        if (allQualifier is null)
        {
            issues.Add("Record has no all mechanism — unlisted senders get a neutral result.");
        }
        else if (allQualifier == "+")
        {
            issues.Add("+all authorizes every server on the internet — replace it with -all or ~all.");
        }

        if (lookups > 10)
        {
            issues.Add($"{lookups} top-level DNS-lookup mechanisms — over the RFC 7208 limit of 10 (permerror). Includes may add more.");
        }

        return new DnsSpfRecordDto(RecordLookupStatus.Found, raw, records.Count, lookups, allQualifier, issues);
    }

    // --- Published (DNS) vs observed (reports) ---

    private static IReadOnlyList<RecordComparisonDto> Compare(DnsDmarcRecordDto dmarc, ObservedPolicyDto? observed)
    {
        if (observed is null || dmarc.Status != RecordLookupStatus.Found)
        {
            return [];
        }

        // Reports omit sp= to mean "same as p"; DNS omitting sp= means the same.
        var publishedSp = dmarc.SubdomainPolicy ?? dmarc.Policy;
        var publishedPct = dmarc.Pct ?? 100;

        return
        [
            new RecordComparisonDto("p", dmarc.Policy, observed.Policy,
                string.Equals(dmarc.Policy, observed.Policy, StringComparison.OrdinalIgnoreCase)),
            new RecordComparisonDto("sp", publishedSp, observed.SubdomainPolicy,
                string.Equals(publishedSp, observed.SubdomainPolicy, StringComparison.OrdinalIgnoreCase)),
            new RecordComparisonDto("pct", publishedPct.ToString(), observed.Pct.ToString(),
                publishedPct == observed.Pct),
            new RecordComparisonDto("adkim", dmarc.DkimAlignment ?? "r", NormalizeAlignment(observed.DkimAlignment),
                AlignmentMatches(dmarc.DkimAlignment, observed.DkimAlignment)),
            new RecordComparisonDto("aspf", dmarc.SpfAlignment ?? "r", NormalizeAlignment(observed.SpfAlignment),
                AlignmentMatches(dmarc.SpfAlignment, observed.SpfAlignment)),
        ];
    }

    // DNS uses r/s; reports store relaxed/strict. Compare on the single-letter form.
    private static string NormalizeAlignment(string value)
        => value.StartsWith('s') ? "s" : "r";

    private static bool AlignmentMatches(string? published, string observed)
        => string.Equals(published ?? "r", NormalizeAlignment(observed), StringComparison.OrdinalIgnoreCase);
}
