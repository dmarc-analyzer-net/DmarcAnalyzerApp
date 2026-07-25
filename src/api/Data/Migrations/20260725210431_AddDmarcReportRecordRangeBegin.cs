using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <summary>
    /// Denormalises the parent report's range start onto every record so analytics can
    /// filter a window with an index range scan instead of hash-joining the whole record
    /// table once per aggregate. See <c>DmarcReportRecord.ReportRangeBeginUtc</c>.
    ///
    /// Deliberately hand-written rather than left as scaffolded. The generated version
    /// added the column NOT NULL with a 0001-01-01 default, which would have stamped
    /// every existing row with a date outside every window — the dashboards would read
    /// empty and the new index would be built over the wrong values. The order below
    /// (nullable, backfill, constrain, then index) is what makes the column correct for
    /// rows that already exist.
    ///
    /// The backfill rewrites every row: ~94s and ~740MB of dead tuples for autovacuum to
    /// reclaim on a 5.3M-row table. It runs as one statement inside the migration's
    /// transaction, so a failure applies nothing and the next boot retries. That exceeds
    /// Npgsql's 30s default, so both callers of MigrateAsync raise the command timeout.
    /// </summary>
    public partial class AddDmarcReportRecordRangeBegin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable first: adding a NOT NULL column to an existing table needs a
            // default, and any default would be a wrong date rather than a missing one.
            migrationBuilder.Sql(@"
                ALTER TABLE dmarc_report_record
                ADD COLUMN ""ReportRangeBeginUtc"" timestamp with time zone NULL;");

            // The long step. An inner join, so a record whose report is missing would stay
            // NULL and fail the constraint below — which is the right way to find out.
            migrationBuilder.Sql(@"
                UPDATE dmarc_report_record rec
                SET ""ReportRangeBeginUtc"" = rep.""RangeBeginUtc""
                FROM dmarc_report rep
                WHERE rec.""DmarcReportId"" = rep.""Id"";");

            // No column default: ingestion always supplies the value, and leaving one
            // would let a future insert that forgets it silently land outside all windows.
            migrationBuilder.Sql(@"
                ALTER TABLE dmarc_report_record
                ALTER COLUMN ""ReportRangeBeginUtc"" SET NOT NULL;");

            // Built last — indexing before the backfill would maintain the index across
            // all 5.3M row updates instead of building it once over settled data.
            migrationBuilder.CreateIndex(
                name: "IX_dmarc_report_record_ReportRangeBeginUtc",
                table: "dmarc_report_record",
                column: "ReportRangeBeginUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dmarc_report_record_ReportRangeBeginUtc",
                table: "dmarc_report_record");

            migrationBuilder.DropColumn(
                name: "ReportRangeBeginUtc",
                table: "dmarc_report_record");
        }
    }
}
