using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMtaStsPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mta_sts_policy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MaxAgeSeconds = table.Column<int>(type: "integer", nullable: false),
                    MxPatterns = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PolicyId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ModeChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mta_sts_policy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mta_sts_policy_domain_DomainId",
                        column: x => x.DomainId,
                        principalTable: "domain",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mta_sts_policy_DomainId",
                table: "mta_sts_policy",
                column: "DomainId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mta_sts_policy");
        }
    }
}
