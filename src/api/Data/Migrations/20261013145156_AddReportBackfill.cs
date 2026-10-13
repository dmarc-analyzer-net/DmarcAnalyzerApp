using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReportBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ParseMessagesRecordedAtUtc",
                table: "dmarc_report",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "report_backfill_candidate",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DmarcReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ValuesHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReportFormat = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NonexistentSubdomainPolicy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Testing = table.Column<bool>(type: "boolean", nullable: true),
                    DiscoveryMethod = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Generator = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReportErrors = table.Column<string>(type: "jsonb", nullable: true),
                    PublishedPct = table.Column<int>(type: "integer", nullable: true),
                    ParseMessages = table.Column<string>(type: "jsonb", nullable: false),
                    ParseWarningCount = table.Column<int>(type: "integer", nullable: false),
                    CollectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_backfill_candidate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_backfill_candidate_dmarc_report_DmarcReportId",
                        column: x => x.DmarcReportId,
                        principalTable: "dmarc_report",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_backfill_state",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Pass = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LastObjectKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ObjectsSeen = table.Column<int>(type: "integer", nullable: false),
                    PayloadsParsed = table.Column<int>(type: "integer", nullable: false),
                    Matched = table.Column<int>(type: "integer", nullable: false),
                    Updated = table.Column<int>(type: "integer", nullable: false),
                    Skipped = table.Column<int>(type: "integer", nullable: false),
                    Ambiguous = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_backfill_state", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_report_backfill_candidate_DmarcReportId_ObjectKey",
                table: "report_backfill_candidate",
                columns: new[] { "DmarcReportId", "ObjectKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_backfill_candidate");

            migrationBuilder.DropTable(
                name: "report_backfill_state");

            migrationBuilder.DropColumn(
                name: "ParseMessagesRecordedAtUtc",
                table: "dmarc_report");
        }
    }
}
