namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>
/// The per-request identity of a machine caller, deliberately separate from
/// <see cref="ICurrentUserContext"/>.
/// <para>
/// A machine is not a user and does not get a role. Reusing the user context would have
/// meant inventing a synthetic <c>agency_user</c> for a service account, which is how a
/// service account ends up able to read the console API — ADR 0010 says no, and keeping
/// the two contexts apart is what enforces it. Nothing that answers "which role is this"
/// can accidentally answer it for a credential.
/// </para>
/// </summary>
public interface IMachineCallerContext
{
    bool IsAuthenticated { get; }
    Guid CredentialId { get; }
    string CredentialName { get; }
    string Kind { get; }

    /// <summary>The source this credential ingests for, and therefore the client its data lands under.</summary>
    Guid ReportSourceId { get; }
}

public sealed class MachineCallerContext : IMachineCallerContext
{
    public bool IsAuthenticated { get; private set; }
    public Guid CredentialId { get; private set; }
    public string CredentialName { get; private set; } = string.Empty;
    public string Kind { get; private set; } = string.Empty;
    public Guid ReportSourceId { get; private set; }

    internal void Set(Guid credentialId, string credentialName, string kind, Guid reportSourceId)
    {
        IsAuthenticated = true;
        CredentialId = credentialId;
        CredentialName = credentialName;
        Kind = kind;
        ReportSourceId = reportSourceId;
    }
}
