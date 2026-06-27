using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OfficeIsolation : Migration
    {
        private static readonly Guid DefaultOfficeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Offices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RegistrationSecretHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Offices", x => x.Id);
                });

            migrationBuilder.Sql($"""
                INSERT INTO "Offices" ("Id", "Name", "RegistrationSecretHash", "CreatedAtUtc", "IsEnabled")
                VALUES ('{DefaultOfficeId}', 'Основной', '', NOW() AT TIME ZONE 'UTC', TRUE);
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "OfficeId",
                table: "Workers",
                type: "uuid",
                nullable: false,
                defaultValue: DefaultOfficeId);

            migrationBuilder.CreateTable(
                name: "PanelUserProfiles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    OfficeId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PanelUserProfiles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_PanelUserProfiles_Offices_OfficeId",
                        column: x => x.OfficeId,
                        principalTable: "Offices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workers_OfficeId",
                table: "Workers",
                column: "OfficeId");

            migrationBuilder.CreateIndex(
                name: "IX_Offices_Name",
                table: "Offices",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PanelUserProfiles_OfficeId",
                table: "PanelUserProfiles",
                column: "OfficeId");

            migrationBuilder.AddForeignKey(
                name: "FK_Workers_Offices_OfficeId",
                table: "Workers",
                column: "OfficeId",
                principalTable: "Offices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Workers_Offices_OfficeId",
                table: "Workers");

            migrationBuilder.DropTable(
                name: "PanelUserProfiles");

            migrationBuilder.DropTable(
                name: "Offices");

            migrationBuilder.DropIndex(
                name: "IX_Workers_OfficeId",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "OfficeId",
                table: "Workers");
        }
    }
}