using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEventClientName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Deliberately not backfilled. Existing rows have no record of what the
            // client was called when they were written, and copying today's name in
            // would invent audit data rather than admit the gap. They stay null and
            // read through to the current name, exactly as before, and age out with
            // the trail's two-year retention.

            migrationBuilder.AddColumn<string>(
                name: "ClientName",
                table: "audit_event",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientName",
                table: "audit_event");
        }
    }
}
