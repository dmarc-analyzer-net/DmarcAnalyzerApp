using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <summary>
    /// Renames the <c>MailboxSourceId</c> foreign key column to <c>ReportSourceId</c> on the
    /// five tables that carry it, following the table rename in the previous migration.
    /// <para>
    /// Same discipline as that migration, and for the same reason: every statement is a
    /// catalog-only rename. <c>RenameColumn</c> and <c>RenameIndex</c> are catalog-only
    /// already, but <c>MigrationBuilder</c> has no <c>RenameConstraint</c>, so the scaffolder
    /// drops and re-adds all five foreign keys — and re-adding one revalidates it by scanning
    /// the whole referencing table. <c>dmarc_report</c> and <c>tls_report_ingest</c> are the
    /// two largest tables in the schema, so that is the difference between an instant upgrade
    /// and a long one on a real install.
    /// </para>
    /// <para>
    /// The target names are the ones the scaffolder generated for this model change, so the
    /// snapshot and the database agree without pinning anything with
    /// <c>HasConstraintName</c>. The one index that *is* pinned —
    /// <c>IX_mailbox_sync_run_ReportSourceId</c>, via <c>HasDatabaseName</c> in the context —
    /// was updated there to match.
    /// </para>
    /// </summary>
    public partial class RenameMailboxSourceIdToReportSourceId : Migration
    {
        /// <summary>
        /// The five tables carrying the foreign key, in a single list so the column, index,
        /// constraint and NOT NULL renames below cannot drift apart.
        /// </summary>
        private static readonly string[] Tables =
        [
            "dmarc_report",
            "dmarc_report_ingest",
            "mailbox_sync_run",
            "smtp_tls_report",
            "tls_report_ingest",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
            => Rename(migrationBuilder, from: "MailboxSourceId", to: "ReportSourceId");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => Rename(migrationBuilder, from: "ReportSourceId", to: "MailboxSourceId");

        /// <summary>
        /// Symmetric in both directions, so <c>Down</c> is the same code with the arguments
        /// swapped rather than a second list of names to keep in step by hand.
        /// </summary>
        private static void Rename(MigrationBuilder migrationBuilder, string from, string to)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.RenameColumn(
                    name: from,
                    table: table,
                    newName: to);

                migrationBuilder.RenameIndex(
                    name: $"IX_{table}_{from}",
                    table: table,
                    newName: $"IX_{table}_{to}");

                migrationBuilder.Sql(
                    $"""
                    ALTER TABLE {table}
                        RENAME CONSTRAINT "FK_{table}_report_source_{from}"
                        TO "FK_{table}_report_source_{to}";
                    """);
            }

            // PostgreSQL 18 names each NOT NULL constraint after the table and column it was
            // created on, and RENAME COLUMN does not follow — the same gap the table rename
            // hit. Left alone, five constraints would keep saying MailboxSourceId on columns
            // that no longer exist under that name. Guarded by an existence check rather than
            // written as five statements, because on PostgreSQL 17 and older NOT NULL is a
            // column attribute with no pg_constraint row to rename.
            foreach (var table in Tables)
            {
                migrationBuilder.Sql(
                    $"""
                    DO $$
                    BEGIN
                        IF EXISTS (
                            SELECT 1 FROM pg_constraint
                            WHERE conrelid = '{table}'::regclass
                              AND contype = 'n'
                              AND conname = '{table}_{from}_not_null'
                        ) THEN
                            ALTER TABLE {table}
                                RENAME CONSTRAINT "{table}_{from}_not_null"
                                TO "{table}_{to}_not_null";
                        END IF;
                    END $$;
                    """);
            }
        }
    }
}
