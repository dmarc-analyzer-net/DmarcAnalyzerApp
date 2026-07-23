using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DmarcAnalyzer.Api.Data;

/// <summary>
/// Design-time factory for `dotnet ef` (migrations). Avoids building the full
/// web host at design time, which needs runtime assets like wwwroot.
/// </summary>
public sealed class DmarcAnalyzerDbContextFactory : IDesignTimeDbContextFactory<DmarcAnalyzerDbContext>
{
    public DmarcAnalyzerDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=dmarc_analyzer;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new DmarcAnalyzerDbContext(options);
    }
}
