using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The dedupe design, asserted from model metadata — the InMemory-viable half of
/// what ON CONFLICT does at runtime (the raw-SQL half is proven by the PR's
/// double-send against Postgres). Guards the decision that the report key has
/// no policy domain (multi-domain reports) and the ledger key replaces it with
/// the organization name, plus the cascade/restrict split that retention and
/// domain-safety depend on.
/// </summary>
public sealed class TlsIngestModelTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    [Fact]
    public void ReportDedupeKey_IsOrgReportIdAndRange()
    {
        using var db = NewDb();
        var entity = db.Model.FindEntityType(typeof(SmtpTlsReport))!;

        var unique = Assert.Single(entity.GetIndexes(), i => i.IsUnique);
        Assert.Equal(
            ["OrganizationName", "ReportId", "RangeBeginUtc", "RangeEndUtc"],
            unique.Properties.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void LedgerDedupeKey_SwapsPolicyDomainForOrganization()
    {
        using var db = NewDb();
        var entity = db.Model.FindEntityType(typeof(TlsReportIngest))!;

        var unique = Assert.Single(entity.GetIndexes(), i => i.IsUnique);
        Assert.Equal(
            ["ClientId", "OrganizationName", "ReportId", "ReportRangeBeginUtc", "ReportRangeEndUtc"],
            unique.Properties.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void CascadesFollowTheDmarcShape()
    {
        using var db = NewDb();

        // Children cascade with their report; the domain FK restricts, so a
        // domain can never vanish out from under report data.
        var policy = db.Model.FindEntityType(typeof(SmtpTlsReportPolicy))!;
        Assert.Equal(DeleteBehavior.Cascade,
            policy.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(SmtpTlsReport)).DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict,
            policy.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(Domain)).DeleteBehavior);

        var detail = db.Model.FindEntityType(typeof(SmtpTlsFailureDetail))!;
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(detail.GetForeignKeys()).DeleteBehavior);
    }
}
