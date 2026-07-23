using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DmarcAnalyzer.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClientViewerRbac : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_client_grant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_client_grant", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_client_grant_agency_user_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "agency_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_user_client_grant_agency_user_UserId",
                        column: x => x.UserId,
                        principalTable: "agency_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_client_grant_client_ClientId",
                        column: x => x.ClientId,
                        principalTable: "client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_client_grant_ClientId",
                table: "user_client_grant",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_user_client_grant_CreatedByUserId",
                table: "user_client_grant",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_client_grant_UserId_ClientId",
                table: "user_client_grant",
                columns: new[] { "UserId", "ClientId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_client_grant");
        }
    }
}
