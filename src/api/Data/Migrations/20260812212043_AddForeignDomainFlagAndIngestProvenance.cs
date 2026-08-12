using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddForeignDomainFlagAndIngestProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowForeignDomains",
                table: "report_source",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Provenance",
                table: "report_ingest_receipt",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProvenanceVersion",
                table: "report_ingest_receipt",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowForeignDomains",
                table: "report_source");

            migrationBuilder.DropColumn(
                name: "Provenance",
                table: "report_ingest_receipt");

            migrationBuilder.DropColumn(
                name: "ProvenanceVersion",
                table: "report_ingest_receipt");
        }
    }
}
