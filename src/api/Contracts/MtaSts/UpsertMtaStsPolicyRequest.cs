namespace DmarcAnalyzer.Api.Contracts.MtaSts;

public sealed class UpsertMtaStsPolicyRequest
{
    public bool Enabled { get; set; } = true;

    /// <summary>enforce, testing or none.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Seconds senders may cache the policy. 3600–31557600.</summary>
    public int MaxAgeSeconds { get; set; }

    /// <summary>mx patterns, e.g. mx1.example.com or *.example.com. Required unless mode is none.</summary>
    public string[] MxPatterns { get; set; } = [];
}
