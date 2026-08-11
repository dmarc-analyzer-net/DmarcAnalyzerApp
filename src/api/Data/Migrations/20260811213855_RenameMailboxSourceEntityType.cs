using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <summary>
    /// Deliberately empty, and not a mistake.
    /// <para>
    /// The CLR type <c>MailboxSource</c> became <c>ReportSource</c>. The table it maps to was
    /// already <c>report_source</c>, so there is no DDL to run — but the model snapshot records
    /// entity types by their CLR name, and leaving it saying <c>MailboxSource</c> would make the
    /// next <c>migrations add</c> diff the new model against a stale one. This migration exists
    /// only to pair that snapshot change with a migration, the way every other snapshot change
    /// in this folder is paired.
    /// </para>
    /// <para>
    /// Applying it writes a row to <c>__EFMigrationsHistory</c> and touches nothing else, in
    /// either direction.
    /// </para>
    /// </summary>
    public partial class RenameMailboxSourceEntityType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
