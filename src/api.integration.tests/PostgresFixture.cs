using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// One PostgreSQL container for the whole run, migrated once.
/// <para>
/// The image is pinned to the same major version the chart and compose files ship, because
/// a test that passes on a different major than production is testing something other than
/// production — and PostgreSQL 18 is the release that changed how NOT NULL constraints are
/// catalogued, which the migrations already have to account for.
/// </para>
/// <para>
/// Migrations are applied rather than <c>EnsureCreated</c>. <c>EnsureCreated</c> builds the
/// schema from the model and would skip the migration chain entirely, so the thing shipped
/// to operators — the chain — would never be exercised. Applying it here means a broken
/// migration fails this suite.
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:18-alpine")
        .WithDatabase("dmarc_analyzer")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public DmarcAnalyzerDbContext CreateContext()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    /// <summary>
    /// Empties every table the ingestion tests touch, so each test starts from a known
    /// state without paying for a container per test.
    /// <para>
    /// Ordered by dependency and done with <c>TRUNCATE ... CASCADE</c> rather than deletes,
    /// because the point is a clean slate, not an exercise of the cascade rules.
    /// </para>
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE
                api_credential,
                report_ingest_receipt,
                dmarc_report_record_dkim_auth_result,
                dmarc_report_record_spf_auth_result,
                dmarc_report_record,
                dmarc_report,
                dmarc_report_ingest,
                smtp_tls_failure_detail,
                smtp_tls_report_policy,
                smtp_tls_report,
                tls_report_ingest,
                domain,
                report_source,
                client
            RESTART IDENTITY CASCADE;
            """);
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
