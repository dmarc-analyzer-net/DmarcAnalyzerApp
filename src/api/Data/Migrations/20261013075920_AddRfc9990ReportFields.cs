using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRfc9990ReportFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows keep their values. PublishedPct stays 100 on them — mostly
            // RFC 7489 reports, where 100 is the real default — and the dropped column
            // default is what lets a report that sends no pct store null from now on.
            // ReportFormat stays null on them, which is how "stored before these columns
            // existed" is told apart from "the reporter did not send it".
            migrationBuilder.AlterColumn<int>(
                name: "PublishedPct",
                table: "dmarc_report",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 100);

            migrationBuilder.AddColumn<string>(
                name: "DiscoveryMethod",
                table: "dmarc_report",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Generator",
                table: "dmarc_report",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NonexistentSubdomainPolicy",
                table: "dmarc_report",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportErrors",
                table: "dmarc_report",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReportFormat",
                table: "dmarc_report",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Testing",
                table: "dmarc_report",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscoveryMethod",
                table: "dmarc_report");

            migrationBuilder.DropColumn(
                name: "Generator",
                table: "dmarc_report");

            migrationBuilder.DropColumn(
                name: "NonexistentSubdomainPolicy",
                table: "dmarc_report");

            migrationBuilder.DropColumn(
                name: "ReportErrors",
                table: "dmarc_report");

            migrationBuilder.DropColumn(
                name: "ReportFormat",
                table: "dmarc_report");

            migrationBuilder.DropColumn(
                name: "Testing",
                table: "dmarc_report");

            // Reports stored without a pct hold null, which NOT NULL below would refuse.
            // 100 is what the column invented for an absent pct before this migration, so
            // rolling back restores exactly that reading.
            migrationBuilder.Sql("""UPDATE "dmarc_report" SET "PublishedPct" = 100 WHERE "PublishedPct" IS NULL;""");

            migrationBuilder.AlterColumn<int>(
                name: "PublishedPct",
                table: "dmarc_report",
                type: "integer",
                nullable: false,
                defaultValue: 100,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
