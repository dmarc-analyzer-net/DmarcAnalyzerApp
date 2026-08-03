using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DmarcAnalyzer.Api.Data;

/// <summary>
/// Design-time factory for `dotnet ef` (migrations). Avoids building the full
/// web host at design time, which needs runtime assets like wwwroot.
/// </summary>
public sealed class DmarcAnalyzerDbContextFactory : IDesignTimeDbContextFactory<DmarcAnalyzerDbContext>
{
    /// <summary>Matches the startup and admin-endpoint migration paths.</summary>
    private const int MigrationCommandTimeoutSeconds = 600;

    public DmarcAnalyzerDbContext CreateDbContext(string[] args)
    {
        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        var connectionString = !string.IsNullOrEmpty(databaseUrl)
            ? ConnectionStringResolver.FromDatabaseUrl(databaseUrl)
            : Environment.GetEnvironmentVariable("ConnectionStrings__Default")
                ?? "Host=localhost;Port=5432;Database=dmarc_analyzer;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            // The only reason this factory exists is `dotnet ef`, and its main job
            // is applying migrations — some of which carry multi-minute backfills.
            // Npgsql's default is 30s, so `dotnet ef database update` used to die
            // partway through AddDmarcReportRecordRangeBegin on a real database
            // while the startup and endpoint paths, which both set 10 minutes,
            // completed. Same budget here so all three behave alike.
            .UseNpgsql(connectionString, npgsql => npgsql.CommandTimeout(MigrationCommandTimeoutSeconds))
            .Options;

        return new DmarcAnalyzerDbContext(options);
    }
}
