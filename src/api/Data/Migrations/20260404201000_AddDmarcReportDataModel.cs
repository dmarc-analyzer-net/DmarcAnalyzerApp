using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDmarcReportDataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dmarc_report",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailboxSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ReportId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RangeBeginUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RangeEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordCount = table.Column<int>(type: "integer", nullable: false),
                    IngestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dmarc_report", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dmarc_report_domain_DomainId",
                        column: x => x.DomainId,
                        principalTable: "domain",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_dmarc_report_mailbox_source_MailboxSourceId",
                        column: x => x.MailboxSourceId,
                        principalTable: "mailbox_source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "dmarc_report_record",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DmarcReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MessageCount = table.Column<int>(type: "integer", nullable: false),
                    Disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DkimResult = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SpfResult = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HeaderFrom = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EnvelopeFrom = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EnvelopeTo = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dmarc_report_record", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dmarc_report_record_dmarc_report_DmarcReportId",
                        column: x => x.DmarcReportId,
                        principalTable: "dmarc_report",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dmarc_report_record_dkim_auth_result",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DmarcReportRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Selector = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HumanResult = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dmarc_report_record_dkim_auth_result", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dmarc_report_record_dkim_auth_result_dmarc_report_record_Dm~",
                        column: x => x.DmarcReportRecordId,
                        principalTable: "dmarc_report_record",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dmarc_report_record_spf_auth_result",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DmarcReportRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HumanResult = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dmarc_report_record_spf_auth_result", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dmarc_report_record_spf_auth_result_dmarc_report_record_Dma~",
                        column: x => x.DmarcReportRecordId,
                        principalTable: "dmarc_report_record",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dmarc_report_DomainId",
                table: "dmarc_report",
                column: "DomainId");

            migrationBuilder.CreateIndex(
                name: "IX_dmarc_report_MailboxSourceId",
                table: "dmarc_report",
                column: "MailboxSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_dmarc_report_DomainId_ReportId_RangeBeginUtc_RangeEndUtc",
                table: "dmarc_report",
                columns: new[] { "DomainId", "ReportId", "RangeBeginUtc", "RangeEndUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dmarc_report_record_DmarcReportId",
                table: "dmarc_report_record",
                column: "DmarcReportId");

            migrationBuilder.CreateIndex(
                name: "IX_dmarc_report_record_dkim_auth_result_DmarcReportRecordId",
                table: "dmarc_report_record_dkim_auth_result",
                column: "DmarcReportRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_dmarc_report_record_spf_auth_result_DmarcReportRecordId",
                table: "dmarc_report_record_spf_auth_result",
                column: "DmarcReportRecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "dmarc_report_record_dkim_auth_result");
            migrationBuilder.DropTable(name: "dmarc_report_record_spf_auth_result");
            migrationBuilder.DropTable(name: "dmarc_report_record");
            migrationBuilder.DropTable(name: "dmarc_report");
        }
    }
}
