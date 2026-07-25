using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeSubdomainPolicyNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SubdomainPolicy",
                table: "dmarc_report",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldDefaultValue: "none");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reports ingested after the Up() carry NULL for "reporter sent no sp".
            // Collapse them back to the old lossy default, or SET NOT NULL fails.
            migrationBuilder.Sql(
                @"UPDATE dmarc_report SET ""SubdomainPolicy"" = 'none' WHERE ""SubdomainPolicy"" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "SubdomainPolicy",
                table: "dmarc_report",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "none",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);
        }
    }
}
