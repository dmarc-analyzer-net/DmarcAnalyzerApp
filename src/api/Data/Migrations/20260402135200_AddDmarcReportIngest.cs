using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDmarcReportIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dmarc_report_ingest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailboxSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyDomain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ReportId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ReportRangeBeginUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReportRangeEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OrganizationName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RecordCount = table.Column<int>(type: "integer", nullable: false),
                    IngestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dmarc_report_ingest", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dmarc_report_ingest_client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dmarc_report_ingest_mailbox_source_MailboxSourceId",
                        column: x => x.MailboxSourceId,
                        principalTable: "mailbox_source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dmarc_report_ingest_ClientId",
                table: "dmarc_report_ingest",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_dmarc_report_ingest_MailboxSourceId",
                table: "dmarc_report_ingest",
                column: "MailboxSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_dmarc_report_ingest_ClientId_PolicyDomain_ReportId_ReportRangeB~",
                table: "dmarc_report_ingest",
                columns: new[] { "ClientId", "PolicyDomain", "ReportId", "ReportRangeBeginUtc", "ReportRangeEndUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dmarc_report_ingest");
        }
    }
}
