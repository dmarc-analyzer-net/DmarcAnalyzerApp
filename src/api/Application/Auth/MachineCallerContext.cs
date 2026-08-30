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
    /// <summary>True once a bearer token has been verified for this request.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The api_credential row that authenticated, for auditing.</summary>
    Guid CredentialId { get; }

    /// <summary>The operator-chosen credential name, for auditing.</summary>
    string CredentialName { get; }

    /// <summary>The credential kind (e.g. report_push) — endpoints require a specific one.</summary>
    string Kind { get; }

    /// <summary>The source this credential ingests for, and therefore the client its data lands under.</summary>
    Guid ReportSourceId { get; }
}

/// <summary>
/// The request-scoped <see cref="IMachineCallerContext"/>. Starts unauthenticated;
/// the bearer-token middleware calls <c>Set</c> once the credential checks out.
/// </summary>
public sealed class MachineCallerContext : IMachineCallerContext
{
    /// <inheritdoc />
    public bool IsAuthenticated { get; private set; }

    /// <inheritdoc />
    public Guid CredentialId { get; private set; }

    /// <inheritdoc />
    public string CredentialName { get; private set; } = string.Empty;

    /// <inheritdoc />
    public string Kind { get; private set; } = string.Empty;

    /// <inheritdoc />
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
