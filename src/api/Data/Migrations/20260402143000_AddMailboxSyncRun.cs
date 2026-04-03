using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxSyncRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mailbox_sync_run",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailboxSourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MessagesScanned = table.Column<int>(type: "integer", nullable: false),
                    AttachmentsProcessed = table.Column<int>(type: "integer", nullable: false),
                    ReportsInserted = table.Column<int>(type: "integer", nullable: false),
                    ReportsSkippedAsDuplicate = table.Column<int>(type: "integer", nullable: false),
                    ParseFailures = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailbox_sync_run", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mailbox_sync_run_mailbox_source_MailboxSourceId",
                        column: x => x.MailboxSourceId,
                        principalTable: "mailbox_source",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_sync_run_MailboxSourceId",
                table: "mailbox_sync_run",
                column: "MailboxSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_sync_run_StartedAtUtc",
                table: "mailbox_sync_run",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mailbox_sync_run");
        }
    }
}
