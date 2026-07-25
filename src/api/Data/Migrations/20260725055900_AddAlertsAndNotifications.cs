using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertsAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AlertComplianceDropPercent",
                table: "client",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AlertMinMessages",
                table: "client",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AlertsEnabled",
                table: "client",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "alert_event",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainId = table.Column<Guid>(type: "uuid", nullable: true),
                    RuleType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_event", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_event_client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_alert_event_domain_DomainId",
                        column: x => x.DomainId,
                        principalTable: "domain",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "notification_recipient",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "both"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_recipient", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notification_recipient_client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_event_ClientId_RuleType_DetectedAtUtc",
                table: "alert_event",
                columns: new[] { "ClientId", "RuleType", "DetectedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_event_DetectedAtUtc",
                table: "alert_event",
                column: "DetectedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_alert_event_DomainId",
                table: "alert_event",
                column: "DomainId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_recipient_ClientId",
                table: "notification_recipient",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_recipient_ClientId_Email",
                table: "notification_recipient",
                columns: new[] { "ClientId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_event");

            migrationBuilder.DropTable(
                name: "notification_recipient");

            migrationBuilder.DropColumn(
                name: "AlertComplianceDropPercent",
                table: "client");

            migrationBuilder.DropColumn(
                name: "AlertMinMessages",
                table: "client");

            migrationBuilder.DropColumn(
                name: "AlertsEnabled",
                table: "client");
        }
    }
}
