using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260911113000_AddPanelAccessVersion")]
public partial class AddPanelAccessVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "AccessVersion",
            table: "PanelUserProfiles",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AccessVersion",
            table: "PanelUserProfiles");
    }
}
