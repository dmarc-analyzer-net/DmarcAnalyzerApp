using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMachineCredentialsAndIngestReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_credential",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReportSourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    TokenId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_credential", x => x.Id);
                    table.ForeignKey(
                        name: "FK_api_credential_report_source_ReportSourceId",
                        column: x => x.ReportSourceId,
                        principalTable: "report_source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "report_ingest_receipt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PayloadCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_ingest_receipt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_ingest_receipt_report_source_ReportSourceId",
                        column: x => x.ReportSourceId,
                        principalTable: "report_source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_credential_ReportSourceId",
                table: "api_credential",
                column: "ReportSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_api_credential_TokenId",
                table: "api_credential",
                column: "TokenId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_ingest_receipt_ReceivedAtUtc",
                table: "report_ingest_receipt",
                column: "ReceivedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_report_ingest_receipt_ReportSourceId_PayloadSha256",
                table: "report_ingest_receipt",
                columns: new[] { "ReportSourceId", "PayloadSha256" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_credential");

            migrationBuilder.DropTable(
                name: "report_ingest_receipt");
        }
    }
}
