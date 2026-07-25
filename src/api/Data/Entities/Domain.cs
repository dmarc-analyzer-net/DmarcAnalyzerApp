namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class Domain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Cache of the DMARC policy published in DNS, so list views can show the real
    /// policy without one lookup per row. Refreshed by the worker's DNS pass and
    /// written back when a domain detail page resolves a different value.
    /// Null until the domain has been checked once.
    /// </summary>
    public string? DnsPolicy { get; set; }

    /// <summary>
    /// Why <see cref="DnsPolicy"/> is what it is: found, missing (no DMARC record
    /// published) or lookup_failed. Without this, a null policy can't be told
    /// apart from "never checked".
    /// </summary>
    public string? DnsLookupStatus { get; set; }

    /// <summary>When the DNS values above were last refreshed.</summary>
    public DateTime? DnsCheckedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Operator-facing changes only. The DNS cache above deliberately does not
    /// bump this — a background refresh is not an edit to the domain.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Client? Client { get; set; }
}
