using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PanelUserBitrixSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PanelUserBitrixSettings",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    WebhookUrlProtected = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    PortalHost = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ValidationStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ValidationMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastValidatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PanelUserBitrixSettings", x => x.UserId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PanelUserBitrixSettings");
        }
    }
}
