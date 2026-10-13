using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddParseMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParseMessages",
                table: "dmarc_report",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParseMessagesOrigin",
                table: "dmarc_report",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParseWarningCount",
                table: "dmarc_report",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParseMessages",
                table: "dmarc_report");

            migrationBuilder.DropColumn(
                name: "ParseMessagesOrigin",
                table: "dmarc_report");

            migrationBuilder.DropColumn(
                name: "ParseWarningCount",
                table: "dmarc_report");
        }
    }
}
