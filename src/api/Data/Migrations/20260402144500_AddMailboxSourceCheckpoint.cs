using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxSourceCheckpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastProcessedUid",
                table: "mailbox_source",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastProcessedUidValidity",
                table: "mailbox_source",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastProcessedUid",
                table: "mailbox_source");

            migrationBuilder.DropColumn(
                name: "LastProcessedUidValidity",
                table: "mailbox_source");
        }
    }
}
