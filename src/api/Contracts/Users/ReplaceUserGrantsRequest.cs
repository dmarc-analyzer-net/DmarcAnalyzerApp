namespace DmarcAnalyzer.Api.Contracts.Users;

public sealed class ReplaceUserGrantsRequest
{
    public List<Guid> ClientIds { get; set; } = [];
}
