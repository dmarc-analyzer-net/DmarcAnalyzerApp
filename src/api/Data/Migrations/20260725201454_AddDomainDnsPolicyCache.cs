using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainDnsPolicyCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DnsCheckedAtUtc",
                table: "domain",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DnsLookupStatus",
                table: "domain",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DnsPolicy",
                table: "domain",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_domain_DnsCheckedAtUtc",
                table: "domain",
                column: "DnsCheckedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_domain_DnsCheckedAtUtc",
                table: "domain");

            migrationBuilder.DropColumn(
                name: "DnsCheckedAtUtc",
                table: "domain");

            migrationBuilder.DropColumn(
                name: "DnsLookupStatus",
                table: "domain");

            migrationBuilder.DropColumn(
                name: "DnsPolicy",
                table: "domain");
        }
    }
}
