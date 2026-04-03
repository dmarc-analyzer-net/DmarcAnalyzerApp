using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxSyncActiveRunUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT
                        "Id",
                        ROW_NUMBER() OVER (
                            PARTITION BY "MailboxSourceId"
                            ORDER BY "StartedAtUtc" DESC, "CreatedAtUtc" DESC, "Id" DESC
                        ) AS rn
                    FROM mailbox_sync_run
                    WHERE "Status" = 'running'
                )
                UPDATE mailbox_sync_run AS msr
                SET
                    "Status" = 'failed',
                    "FinishedAtUtc" = COALESCE(msr."FinishedAtUtc", NOW()),
                    "Error" = COALESCE(msr."Error", 'auto-closed during active-run uniqueness migration')
                FROM ranked r
                WHERE msr."Id" = r."Id" AND r.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_sync_run_active_unique",
                table: "mailbox_sync_run",
                column: "MailboxSourceId",
                unique: true,
                filter: "\"Status\" = 'running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mailbox_sync_run_active_unique",
                table: "mailbox_sync_run");
        }
    }
}
