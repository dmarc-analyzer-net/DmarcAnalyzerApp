using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <summary>
    /// Renames <c>mailbox_source</c> to <c>report_source</c>, along with every constraint and
    /// index whose name embedded the old table name.
    /// <para>
    /// Every statement here is a catalog-only rename: no table is rewritten, no index rebuilt,
    /// no constraint revalidated, whatever the row counts are. That is deliberate, and it is
    /// why the constraint renames are raw SQL. <c>MigrationBuilder</c> has <c>RenameTable</c>,
    /// <c>RenameColumn</c> and <c>RenameIndex</c> but no <c>RenameConstraint</c>, so the
    /// scaffolder expresses a constraint-name change as drop-then-add — and those are not the
    /// same operation. <c>AddPrimaryKey</c> rebuilds the backing index under an ACCESS
    /// EXCLUSIVE lock, and each <c>AddForeignKey</c> revalidates the constraint by scanning the
    /// whole referencing table: here that would mean full scans of <c>dmarc_report</c> and
    /// <c>tls_report_ingest</c>, the two largest tables in the schema, on an install with real
    /// data. <c>ALTER TABLE ... RENAME CONSTRAINT</c> touches only the catalog.
    /// </para>
    /// <para>
    /// The new names are not free choices. EF derives constraint and index names by convention
    /// from the table names, and because these renames go through <c>Sql</c> the model differ
    /// never sees them — it simply assumes the convention held. Each target below is the exact
    /// name the scaffolder generated for this model change, so the snapshot and the database
    /// agree and later migrations stay clean. Changing one without pinning it with
    /// <c>HasConstraintName</c> would make the next unrelated <c>migrations add</c> emit drift.
    /// </para>
    /// <para>
    /// Identifiers are quoted because the constraint names are mixed case: PostgreSQL folds
    /// unquoted identifiers to lower case, so <c>PK_mailbox_source</c> unquoted would look for
    /// a <c>pk_mailbox_source</c> that does not exist.
    /// </para>
    /// </summary>
    public partial class RenameMailboxSourceToReportSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "mailbox_source",
                newName: "report_source");

            migrationBuilder.RenameIndex(
                name: "IX_mailbox_source_DefaultClientId",
                table: "report_source",
                newName: "IX_report_source_DefaultClientId");

            // Renaming a primary key constraint renames its backing index too, so this needs
            // no companion RenameIndex.
            migrationBuilder.Sql(
                """
                ALTER TABLE report_source
                    RENAME CONSTRAINT "PK_mailbox_source" TO "PK_report_source";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE report_source
                    RENAME CONSTRAINT "FK_mailbox_source_client_DefaultClientId"
                    TO "FK_report_source_client_DefaultClientId";
                """);

            // The five inbound foreign keys live on their own tables, but EF's naming
            // convention embeds the *principal* table, so renaming this table renames them.
            // The IX_*_MailboxSourceId indexes beside them are named for the column and are
            // deliberately left alone: the column keeps its name in this change.
            migrationBuilder.Sql(
                """
                ALTER TABLE dmarc_report
                    RENAME CONSTRAINT "FK_dmarc_report_mailbox_source_MailboxSourceId"
                    TO "FK_dmarc_report_report_source_MailboxSourceId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE dmarc_report_ingest
                    RENAME CONSTRAINT "FK_dmarc_report_ingest_mailbox_source_MailboxSourceId"
                    TO "FK_dmarc_report_ingest_report_source_MailboxSourceId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE mailbox_sync_run
                    RENAME CONSTRAINT "FK_mailbox_sync_run_mailbox_source_MailboxSourceId"
                    TO "FK_mailbox_sync_run_report_source_MailboxSourceId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE smtp_tls_report
                    RENAME CONSTRAINT "FK_smtp_tls_report_mailbox_source_MailboxSourceId"
                    TO "FK_smtp_tls_report_report_source_MailboxSourceId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE tls_report_ingest
                    RENAME CONSTRAINT "FK_tls_report_ingest_mailbox_source_MailboxSourceId"
                    TO "FK_tls_report_ingest_report_source_MailboxSourceId";
                """);

            // PostgreSQL 18 gives every NOT NULL its own catalogued constraint, named after
            // the table it was created on, and RENAME TO does not follow. Without this, every
            // install — fresh ones included, since a new database still creates the table
            // under the old name and renames it here — keeps thirteen
            // mailbox_source_*_not_null constraints on a table called report_source, which is
            // what anyone reading \d or a pg_dump would see. Nothing functional depends on
            // these names and EF has no concept of them, so this is hygiene only.
            //
            // Written as a loop over the catalog rather than thirteen statements because the
            // set is version-dependent: on PostgreSQL 17 and older, NOT NULL is a column
            // attribute with no pg_constraint row at all, so the loop simply finds nothing.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    constraint_name text;
                BEGIN
                    FOR constraint_name IN
                        SELECT conname
                        FROM pg_constraint
                        WHERE conrelid = 'report_source'::regclass
                          AND contype = 'n'
                          AND starts_with(conname, 'mailbox_source_')
                    LOOP
                        EXECUTE format(
                            'ALTER TABLE report_source RENAME CONSTRAINT %I TO %I',
                            constraint_name,
                            'report_source_' || substring(constraint_name from length('mailbox_source_') + 1));
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    constraint_name text;
                BEGIN
                    FOR constraint_name IN
                        SELECT conname
                        FROM pg_constraint
                        WHERE conrelid = 'report_source'::regclass
                          AND contype = 'n'
                          AND starts_with(conname, 'report_source_')
                    LOOP
                        EXECUTE format(
                            'ALTER TABLE report_source RENAME CONSTRAINT %I TO %I',
                            constraint_name,
                            'mailbox_source_' || substring(constraint_name from length('report_source_') + 1));
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE tls_report_ingest
                    RENAME CONSTRAINT "FK_tls_report_ingest_report_source_MailboxSourceId"
                    TO "FK_tls_report_ingest_mailbox_source_MailboxSourceId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE smtp_tls_report
                    RENAME CONSTRAINT "FK_smtp_tls_report_report_source_MailboxSourceId"
                    TO "FK_smtp_tls_report_mailbox_source_MailboxSourceId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE mailbox_sync_run
                    RENAME CONSTRAINT "FK_mailbox_sync_run_report_source_MailboxSourceId"
                    TO "FK_mailbox_sync_run_mailbox_source_MailboxSourceId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE dmarc_report_ingest
                    RENAME CONSTRAINT "FK_dmarc_report_ingest_report_source_MailboxSourceId"
                    TO "FK_dmarc_report_ingest_mailbox_source_MailboxSourceId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE dmarc_report
                    RENAME CONSTRAINT "FK_dmarc_report_report_source_MailboxSourceId"
                    TO "FK_dmarc_report_mailbox_source_MailboxSourceId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE report_source
                    RENAME CONSTRAINT "FK_report_source_client_DefaultClientId"
                    TO "FK_mailbox_source_client_DefaultClientId";
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE report_source
                    RENAME CONSTRAINT "PK_report_source" TO "PK_mailbox_source";
                """);

            migrationBuilder.RenameIndex(
                name: "IX_report_source_DefaultClientId",
                table: "report_source",
                newName: "IX_mailbox_source_DefaultClientId");

            migrationBuilder.RenameTable(
                name: "report_source",
                newName: "mailbox_source");
        }
    }
}
