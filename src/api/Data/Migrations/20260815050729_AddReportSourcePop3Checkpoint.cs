using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReportSourcePop3Checkpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastProcessedUidl",
                table: "report_source",
                type: "character varying(70)",
                maxLength: 70,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastProcessedUidl",
                table: "report_source");
        }
    }
}
