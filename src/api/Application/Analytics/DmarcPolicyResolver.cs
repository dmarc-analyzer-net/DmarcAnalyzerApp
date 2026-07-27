namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>
/// The policy a receiver actually applies to a domain, and where it came from.
/// </summary>
/// <param name="Record">
/// The record that was found, at this domain or an ancestor. Its own <c>Policy</c> is the
/// <c>p=</c> tag as published; use <see cref="Policy"/> for the value that applies here.
/// </param>
/// <param name="Policy">
/// The effective policy. The domain's own <c>p=</c> when it publishes a record; otherwise the
/// ancestor's <c>sp=</c> if it has one, else the ancestor's <c>p=</c>.
/// </param>
/// <param name="Status">
/// <c>found</c> (own record), <c>inherited</c> (an ancestor's), <c>missing</c> (nothing
/// anywhere) or <c>lookup_failed</c>.
/// </param>
/// <param name="InheritedFrom">The ancestor the policy came from, null unless inherited.</param>
public sealed record EffectiveDmarcPolicy(
    DnsDmarcRecordDto Record,
    string? Policy,
    string Status,
    string? InheritedFrom);

public interface IDmarcPolicyResolver
{
    Task<EffectiveDmarcPolicy> ResolveAsync(string domainName, CancellationToken ct);
}

/// <summary>
/// Resolves the policy that applies to a domain by walking up the DNS tree, which is what a
/// receiver does.
/// <para>
/// A subdomain that publishes no DMARC record is not unprotected: RFC 7489 §6.6.3 has the
/// receiver fall back to the organisational domain and apply its <c>sp=</c>, or its <c>p=</c>
/// when there is no <c>sp=</c>. Looking only at <c>_dmarc.{domain}</c> reported six domains in
/// one real instance as having no policy when five of them were in fact covered by
/// <c>p=reject</c> — the console called them unprotected while receivers were rejecting for
/// them.
/// </para>
/// <para>
/// A tree walk rather than a Public Suffix List lookup. It is what DMARCbis specifies, it
/// needs no data file to keep current, and the first record found going up is by definition
/// the one a receiver uses. The known limitation is the flip side of having no PSL: a domain
/// sitting directly under a multi-label public suffix could in principle inherit from that
/// suffix, if a registry published a DMARC record at, say, <c>_dmarc.co.uk</c>. Registries do
/// not, and the walk below stops before single-label names, so the exposure is narrow and
/// visible — the inherited-from name is stored and shown, so a wrong answer is legible rather
/// than silent.
/// </para>
/// </summary>
public sealed class DmarcPolicyResolver(IDnsTxtResolver dns) : IDmarcPolicyResolver
{
    /// <summary>
    /// How far up to walk. DMARCbis bounds the walk rather than going to the root; five steps
    /// covers any realistic sending subdomain (<c>mail.eu.corp.example.co.uk</c> is four).
    /// </summary>
    private const int MaxAncestors = 5;

    public async Task<EffectiveDmarcPolicy> ResolveAsync(string domainName, CancellationToken ct)
    {
        var name = (domainName ?? string.Empty).Trim().Trim('.');

        var own = RecordInspectionService.ParseDmarc(await dns.ResolveAsync($"_dmarc.{name}", ct));
        if (own.Status == RecordLookupStatus.Found)
        {
            return new EffectiveDmarcPolicy(own, own.Policy, RecordLookupStatus.Found, null);
        }

        // A lookup that failed says nothing about ancestors, and guessing from one would be
        // worse than admitting we do not know. Callers keep the last known value on this.
        if (own.Status == RecordLookupStatus.LookupFailed)
        {
            return new EffectiveDmarcPolicy(own, null, RecordLookupStatus.LookupFailed, null);
        }

        foreach (var ancestor in Ancestors(name))
        {
            ct.ThrowIfCancellationRequested();

            var record = RecordInspectionService.ParseDmarc(
                await dns.ResolveAsync($"_dmarc.{ancestor}", ct));

            if (record.Status != RecordLookupStatus.Found)
            {
                // Includes a failed lookup partway up: keep walking rather than stopping, so
                // one flaky ancestor does not hide a policy that is published above it.
                continue;
            }

            // sp= exists precisely to say "this is what my subdomains get", so it wins here.
            // Without it the organisational policy applies unchanged.
            var inherited = string.IsNullOrWhiteSpace(record.SubdomainPolicy)
                ? record.Policy
                : record.SubdomainPolicy;

            return new EffectiveDmarcPolicy(
                record, inherited, RecordLookupStatus.Inherited, ancestor);
        }

        return new EffectiveDmarcPolicy(own, null, RecordLookupStatus.Missing, null);
    }

    /// <summary>
    /// Each parent of <paramref name="name"/>, nearest first, stopping before single-label
    /// names — a TLD is never an organisational domain, and querying one wastes a lookup.
    /// </summary>
    public static IEnumerable<string> Ancestors(string name)
    {
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (var skip = 1; skip <= MaxAncestors; skip++)
        {
            var remaining = labels.Length - skip;
            if (remaining < 2)
            {
                yield break;
            }

            yield return string.Join('.', labels.Skip(skip));
        }
    }
}
