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
    IDnsTxtResolver dns,
    IDmarcPolicyResolver policyResolver) : IRecordInspectionService
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

        // Same walk the list views use. Without it this card said "mail receivers apply no
        // policy" for a subdomain the Domains list showed as reject — two screens, one domain,
        // opposite answers.
        var dmarcTask = policyResolver.ResolveAsync(domain.Name, ct);
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

        var effective = await dmarcTask;
        var dmarc = DescribeEffective(effective, domain.Name);
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


    /// <summary>
    /// Presents the effective policy as this card's DMARC record. When the policy is inherited
    /// the ancestor's record is what a receiver reads, so that is what is shown — with the
    /// "nothing published" complaint replaced by where the policy actually comes from, and the
    /// effective value substituted for the ancestor's p= so sp= is reflected.
    /// </summary>
    private static DnsDmarcRecordDto DescribeEffective(EffectiveDmarcPolicy effective, string domainName)
    {
        if (effective.Status != RecordLookupStatus.Inherited)
        {
            return effective.Record;
        }

        var issues = new List<string>
        {
            $"{domainName} publishes no DMARC record. Receivers apply {effective.InheritedFrom}'s " +
            $"policy, so this domain is effectively p={effective.Policy}. Publish a record here " +
            "only if it should differ.",
        };

        // The ancestor's own complaints are about the ancestor, except the ones that would
        // read as this domain's problem. Keep the substantive ones.
        issues.AddRange(effective.Record.Issues.Where(x => !x.StartsWith("No DMARC record", StringComparison.Ordinal)));

        return effective.Record with
        {
            Status = RecordLookupStatus.Inherited,
            Policy = effective.Policy,
            Issues = issues,
        };
    }

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

        var subdomainPolicy = tags.GetValueOrDefault("sp");
        // A published sp weaker than p is a real gap, not a reporting artifact:
        // subdomains are enforced at the weaker level while the org domain is not.
        // The comparison panel deliberately never flags this, so it is raised here.
        if (PolicyStrength(subdomainPolicy) is { } spStrength
            && PolicyStrength(policy) is { } pStrength
            && spStrength < pStrength)
        {
            issues.Add(
                $"sp={subdomainPolicy} is weaker than p={policy} — subdomains are not protected at the same level.");
        }

        return new DnsDmarcRecordDto(
            RecordLookupStatus.Found,
            raw,
            policy,
            subdomainPolicy,
            pct,
            rua,
            tags.GetValueOrDefault("ruf"),
            tags.GetValueOrDefault("adkim"),
            tags.GetValueOrDefault("aspf"),
            issues);
    }

    /// <summary>none &lt; quarantine &lt; reject; null for anything unrecognized.</summary>
    private static int? PolicyStrength(string? policy) => policy?.Trim().ToLowerInvariant() switch
    {
        "none" => 0,
        "quarantine" => 1,
        "reject" => 2,
        _ => null,
    };

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

        var publishedPct = dmarc.Pct ?? 100;

        return
        [
            new RecordComparisonDto("p", dmarc.Policy, observed.Policy,
                Verdict(string.Equals(dmarc.Policy, observed.Policy, StringComparison.OrdinalIgnoreCase))),
            CompareSubdomainPolicy(dmarc, observed),
            new RecordComparisonDto("pct", publishedPct.ToString(), observed.Pct.ToString(),
                Verdict(publishedPct == observed.Pct)),
            new RecordComparisonDto("adkim", dmarc.DkimAlignment ?? "r", NormalizeAlignment(observed.DkimAlignment),
                Verdict(AlignmentMatches(dmarc.DkimAlignment, observed.DkimAlignment))),
            new RecordComparisonDto("aspf", dmarc.SpfAlignment ?? "r", NormalizeAlignment(observed.SpfAlignment),
                Verdict(AlignmentMatches(dmarc.SpfAlignment, observed.SpfAlignment))),
        ];
    }

    /// <summary>
    /// sp cannot be compared like the other tags. When DNS publishes no sp, RFC 7489
    /// §6.3 makes the subdomain policy p by derivation — the published record is
    /// authoritative and there is nothing a reporter can contradict. Reporters
    /// disagree here anyway: for one domain we see eight orgs echo sp=reject and six
    /// echo sp=none for the same record, because the aggregate-report XSD defaults sp
    /// to "none". Flagging that as a difference blames the customer for a reporter's
    /// quirk, so only an explicitly published sp is ever compared.
    /// </summary>
    private static RecordComparisonDto CompareSubdomainPolicy(DnsDmarcRecordDto dmarc, ObservedPolicyDto observed)
    {
        if (dmarc.SubdomainPolicy is null)
        {
            return new RecordComparisonDto("sp", null, observed.SubdomainPolicy,
                RecordComparisonStatus.Inherited,
                $"Not published — subdomains inherit p={dmarc.Policy ?? "none"}.");
        }

        if (observed.SubdomainPolicy is null)
        {
            return new RecordComparisonDto("sp", dmarc.SubdomainPolicy, null,
                RecordComparisonStatus.NotReported,
                $"{observed.ReportedBy} sent no sp value in its last report.");
        }

        return new RecordComparisonDto("sp", dmarc.SubdomainPolicy, observed.SubdomainPolicy,
            Verdict(string.Equals(dmarc.SubdomainPolicy, observed.SubdomainPolicy, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Verdict(bool matches)
        => matches ? RecordComparisonStatus.Match : RecordComparisonStatus.Differs;

    // DNS uses r/s; reports store relaxed/strict. Compare on the single-letter form.
    private static string NormalizeAlignment(string value)
        => value.StartsWith('s') ? "s" : "r";

    private static bool AlignmentMatches(string? published, string observed)
        => string.Equals(published ?? "r", NormalizeAlignment(observed), StringComparison.OrdinalIgnoreCase);
}
