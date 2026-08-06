namespace DmarcAnalyzer.Api.Contracts.MtaSts;

/// <summary>
/// Applies one policy shape to several of a client's domains — the
/// same-provider-fleet case, where fifty domains share identical mx patterns.
/// Each domain keeps its own row and its own id-bump: only the domains whose
/// rendered policy actually changes get a new id and a TXT record to update.
/// </summary>
public sealed class BulkApplyMtaStsPolicyRequest
{
    /// <summary>Domains to apply to; every id must belong to the client in the route.</summary>
    public Guid[] DomainIds { get; set; } = [];

    /// <summary>Apply to every active domain of the client instead of listing ids.</summary>
    public bool AllDomains { get; set; }

    public bool Enabled { get; set; } = true;
    public string Mode { get; set; } = string.Empty;
    public int MaxAgeSeconds { get; set; }
    public string[] MxPatterns { get; set; } = [];
}
