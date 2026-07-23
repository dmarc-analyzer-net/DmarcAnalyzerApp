using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishedDmarcPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DkimAlignment",
                table: "dmarc_report",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "relaxed");

            migrationBuilder.AddColumn<int>(
                name: "PublishedPct",
                table: "dmarc_report",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<string>(
                name: "PublishedPolicy",
                table: "dmarc_report",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<string>(
                name: "SpfAlignment",
                table: "dmarc_report",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "relaxed");

            migrationBuilder.AddColumn<string>(
                name: "SubdomainPolicy",
                table: "dmarc_report",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "none");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DkimAlignment",
                table: "dmarc_report");

            migrationBuilder.DropColumn(
                name: "PublishedPct",
                table: "dmarc_report");

            migrationBuilder.DropColumn(
                name: "PublishedPolicy",
                table: "dmarc_report");

            migrationBuilder.DropColumn(
                name: "SpfAlignment",
                table: "dmarc_report");

            migrationBuilder.DropColumn(
                name: "SubdomainPolicy",
                table: "dmarc_report");
        }
    }
}
