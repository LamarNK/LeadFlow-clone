using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260822120000_AddPanelUserPresenceHours")]
public partial class AddPanelUserPresenceHours : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PanelUserPresenceHours",
            columns: table => new
            {
                UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                HourUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PanelUserPresenceHours", x => new { x.UserId, x.HourUtc });
            });

        migrationBuilder.CreateIndex(
            name: "IX_PanelUserPresenceHours_HourUtc",
            table: "PanelUserPresenceHours",
            column: "HourUtc");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PanelUserPresenceHours");
    }
}