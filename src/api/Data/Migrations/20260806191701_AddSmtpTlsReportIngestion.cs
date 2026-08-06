using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSmtpTlsReportIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TlsReportsInserted",
                table: "mailbox_sync_run",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TlsReportsSkippedAsDuplicate",
                table: "mailbox_sync_run",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "smtp_tls_report",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailboxSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ReportId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContactInfo = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    RangeBeginUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RangeEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PolicyCount = table.Column<int>(type: "integer", nullable: false),
                    TotalSuccessfulSessionCount = table.Column<long>(type: "bigint", nullable: false),
                    TotalFailureSessionCount = table.Column<long>(type: "bigint", nullable: false),
                    IngestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_smtp_tls_report", x => x.Id);
                    table.ForeignKey(
                        name: "FK_smtp_tls_report_mailbox_source_MailboxSourceId",
                        column: x => x.MailboxSourceId,
                        principalTable: "mailbox_source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tls_report_ingest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    MailboxSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ReportId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ReportRangeBeginUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReportRangeEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PolicyDomains = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PolicyCount = table.Column<int>(type: "integer", nullable: false),
                    TotalSuccessfulSessionCount = table.Column<long>(type: "bigint", nullable: false),
                    TotalFailureSessionCount = table.Column<long>(type: "bigint", nullable: false),
                    ContactInfo = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    IngestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tls_report_ingest", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tls_report_ingest_client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tls_report_ingest_mailbox_source_MailboxSourceId",
                        column: x => x.MailboxSourceId,
                        principalTable: "mailbox_source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "smtp_tls_report_policy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SmtpTlsReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PolicyDomain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PolicyString = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    MxHostPatterns = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SuccessfulSessionCount = table.Column<long>(type: "bigint", nullable: false),
                    FailureSessionCount = table.Column<long>(type: "bigint", nullable: false),
                    ReportRangeBeginUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReportRangeEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_smtp_tls_report_policy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_smtp_tls_report_policy_domain_DomainId",
                        column: x => x.DomainId,
                        principalTable: "domain",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_smtp_tls_report_policy_smtp_tls_report_SmtpTlsReportId",
                        column: x => x.SmtpTlsReportId,
                        principalTable: "smtp_tls_report",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "smtp_tls_failure_detail",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SmtpTlsReportPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FailureCategory = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SendingMtaIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReceivingMxHostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReceivingMxHelo = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReceivingIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailedSessionCount = table.Column<long>(type: "bigint", nullable: false),
                    AdditionalInformation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FailureReasonCode = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_smtp_tls_failure_detail", x => x.Id);
                    table.ForeignKey(
                        name: "FK_smtp_tls_failure_detail_smtp_tls_report_policy_SmtpTlsRepor~",
                        column: x => x.SmtpTlsReportPolicyId,
                        principalTable: "smtp_tls_report_policy",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_smtp_tls_failure_detail_SmtpTlsReportPolicyId",
                table: "smtp_tls_failure_detail",
                column: "SmtpTlsReportPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_smtp_tls_report_MailboxSourceId",
                table: "smtp_tls_report",
                column: "MailboxSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_smtp_tls_report_OrganizationName_ReportId_RangeBeginUtc_Ran~",
                table: "smtp_tls_report",
                columns: new[] { "OrganizationName", "ReportId", "RangeBeginUtc", "RangeEndUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_smtp_tls_report_RangeEndUtc",
                table: "smtp_tls_report",
                column: "RangeEndUtc");

            migrationBuilder.CreateIndex(
                name: "IX_smtp_tls_report_policy_DomainId_ReportRangeBeginUtc",
                table: "smtp_tls_report_policy",
                columns: new[] { "DomainId", "ReportRangeBeginUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_smtp_tls_report_policy_ReportRangeEndUtc",
                table: "smtp_tls_report_policy",
                column: "ReportRangeEndUtc");

            migrationBuilder.CreateIndex(
                name: "IX_smtp_tls_report_policy_SmtpTlsReportId",
                table: "smtp_tls_report_policy",
                column: "SmtpTlsReportId");

            migrationBuilder.CreateIndex(
                name: "IX_tls_report_ingest_ClientId",
                table: "tls_report_ingest",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_tls_report_ingest_ClientId_OrganizationName_ReportId_Report~",
                table: "tls_report_ingest",
                columns: new[] { "ClientId", "OrganizationName", "ReportId", "ReportRangeBeginUtc", "ReportRangeEndUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tls_report_ingest_MailboxSourceId",
                table: "tls_report_ingest",
                column: "MailboxSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_tls_report_ingest_ReportRangeEndUtc",
                table: "tls_report_ingest",
                column: "ReportRangeEndUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "smtp_tls_failure_detail");

            migrationBuilder.DropTable(
                name: "tls_report_ingest");

            migrationBuilder.DropTable(
                name: "smtp_tls_report_policy");

            migrationBuilder.DropTable(
                name: "smtp_tls_report");

            migrationBuilder.DropColumn(
                name: "TlsReportsInserted",
                table: "mailbox_sync_run");

            migrationBuilder.DropColumn(
                name: "TlsReportsSkippedAsDuplicate",
                table: "mailbox_sync_run");
        }
    }
}
