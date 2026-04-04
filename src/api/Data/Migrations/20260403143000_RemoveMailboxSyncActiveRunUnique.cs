using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMailboxSyncActiveRunUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_mailbox_sync_run_active_unique",
                table: "mailbox_sync_run");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_mailbox_sync_run_active_unique",
                table: "mailbox_sync_run",
                column: "MailboxSourceId",
                unique: true,
                filter: "\"Status\" = 'running'");
        }
    }
}
