using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDigestRoutingAndThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_digest_delivery_ClientId_PeriodStartUtc",
                table: "digest_delivery");

            // Existing rows keep RecipientEmail null: the digest reads that as "this
            // client's month already went to everyone", so the upgrade re-sends nothing.
            // Existing several-clients recipients take the 'rollup' default — one combined
            // mail instead of one per client is the point of this change.
            migrationBuilder.AddColumn<string>(
                name: "DigestDefaultMode",
                table: "notification_recipient",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "rollup");

            migrationBuilder.AlterColumn<Guid>(
                name: "ClientId",
                table: "digest_delivery",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "ClientCount",
                table: "digest_delivery",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "RecipientEmail",
                table: "digest_delivery",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DigestThresholds",
                table: "client",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "notification_recipient_client",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    DigestMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_recipient_client", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notification_recipient_client_client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_recipient_client_notification_recipient_Recipi~",
                        column: x => x.RecipientId,
                        principalTable: "notification_recipient",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_digest_delivery_ClientId",
                table: "digest_delivery",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_digest_delivery_RecipientEmail_ClientId_PeriodStartUtc",
                table: "digest_delivery",
                columns: new[] { "RecipientEmail", "ClientId", "PeriodStartUtc" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_notification_recipient_client_ClientId",
                table: "notification_recipient_client",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_recipient_client_RecipientId_ClientId",
                table: "notification_recipient_client",
                columns: new[] { "RecipientId", "ClientId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_recipient_client");

            migrationBuilder.DropIndex(
                name: "IX_digest_delivery_ClientId",
                table: "digest_delivery");

            migrationBuilder.DropIndex(
                name: "IX_digest_delivery_RecipientEmail_ClientId_PeriodStartUtc",
                table: "digest_delivery");

            // The old (ClientId, PeriodStartUtc) index cannot hold per-recipient or roll-up
            // rows. Dropping them means a downgrade may re-send the latest month once.
            migrationBuilder.Sql(
                "DELETE FROM digest_delivery WHERE \"RecipientEmail\" IS NOT NULL OR \"ClientId\" IS NULL;");

            migrationBuilder.DropColumn(
                name: "DigestDefaultMode",
                table: "notification_recipient");

            migrationBuilder.DropColumn(
                name: "ClientCount",
                table: "digest_delivery");

            migrationBuilder.DropColumn(
                name: "RecipientEmail",
                table: "digest_delivery");

            migrationBuilder.DropColumn(
                name: "DigestThresholds",
                table: "client");

            migrationBuilder.AlterColumn<Guid>(
                name: "ClientId",
                table: "digest_delivery",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_digest_delivery_ClientId_PeriodStartUtc",
                table: "digest_delivery",
                columns: new[] { "ClientId", "PeriodStartUtc" },
                unique: true);
        }
    }
}
