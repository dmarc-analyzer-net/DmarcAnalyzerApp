namespace DmarcAnalyzer.Api.Contracts.Users;

/// <summary>Body of PUT /api/v1/users/{id}/grants — the full desired set, not a delta.</summary>
public sealed class ReplaceUserGrantsRequest
{
    public List<Guid> ClientIds { get; set; } = [];
}
