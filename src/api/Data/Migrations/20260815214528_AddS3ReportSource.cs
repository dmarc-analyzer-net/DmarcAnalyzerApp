using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddS3ReportSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastProcessedObjectAtUtc",
                table: "report_source",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastProcessedObjectKey",
                table: "report_source",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "S3Bucket",
                table: "report_source",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "S3Endpoint",
                table: "report_source",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "S3ForcePathStyle",
                table: "report_source",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "S3Prefix",
                table: "report_source",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "S3Region",
                table: "report_source",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastProcessedObjectAtUtc",
                table: "report_source");

            migrationBuilder.DropColumn(
                name: "LastProcessedObjectKey",
                table: "report_source");

            migrationBuilder.DropColumn(
                name: "S3Bucket",
                table: "report_source");

            migrationBuilder.DropColumn(
                name: "S3Endpoint",
                table: "report_source");

            migrationBuilder.DropColumn(
                name: "S3ForcePathStyle",
                table: "report_source");

            migrationBuilder.DropColumn(
                name: "S3Prefix",
                table: "report_source");

            migrationBuilder.DropColumn(
                name: "S3Region",
                table: "report_source");
        }
    }
}
