using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMtaStsState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mta_sts_state",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainId = table.Column<Guid>(type: "uuid", nullable: false),
                    DnsRecordStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RawRecord = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PolicyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousPolicyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PolicyIdChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FetchStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FetchDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastFetchOkAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PolicyValid = table.Column<bool>(type: "boolean", nullable: true),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MaxAgeSeconds = table.Column<long>(type: "bigint", nullable: true),
                    PolicyBody = table.Column<string>(type: "text", nullable: true),
                    MxLookupStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MxHostsJson = table.Column<string>(type: "text", nullable: true),
                    UnmatchedMxHostsJson = table.Column<string>(type: "text", nullable: true),
                    IssuesJson = table.Column<string>(type: "text", nullable: true),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mta_sts_state", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mta_sts_state_domain_DomainId",
                        column: x => x.DomainId,
                        principalTable: "domain",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mta_sts_state_DomainId",
                table: "mta_sts_state",
                column: "DomainId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mta_sts_state_LastCheckedAtUtc",
                table: "mta_sts_state",
                column: "LastCheckedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mta_sts_state");
        }
    }
}
