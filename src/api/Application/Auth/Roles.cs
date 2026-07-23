namespace DmarcAnalyzer.Api.Application.Auth;

public static class Roles
{
    public const string AgencyAdmin = "agency_admin";
    public const string AgencyAnalyst = "agency_analyst";
    public const string ClientViewer = "client_viewer";

    public static readonly string[] All = [AgencyAdmin, AgencyAnalyst, ClientViewer];

    public static bool IsValid(string role) => All.Contains(role);

    public static bool IsAgencyStaff(string role) => role is AgencyAdmin or AgencyAnalyst;
}
