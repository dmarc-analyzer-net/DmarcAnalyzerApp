namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>The three roles (ADR 0007). Stored as strings on the user row; there is no role table.</summary>
public static class Roles
{
    /// <summary>Everything, including user administration and destructive operations.</summary>
    public const string AgencyAdmin = "agency_admin";

    /// <summary>All clients, read + operational actions; no user administration.</summary>
    public const string AgencyAnalyst = "agency_analyst";

    /// <summary>Read-only, and only the clients granted via user_client_grant.</summary>
    public const string ClientViewer = "client_viewer";

    /// <summary>Every valid role value, for validation and iteration.</summary>
    public static readonly string[] All = [AgencyAdmin, AgencyAnalyst, ClientViewer];

    /// <summary>Guards role values arriving from requests and imports.</summary>
    public static bool IsValid(string role) => All.Contains(role);

    /// <summary>The staff test: admin or analyst, the roles that see every client.</summary>
    public static bool IsAgencyStaff(string role) => role is AgencyAdmin or AgencyAnalyst;
}
