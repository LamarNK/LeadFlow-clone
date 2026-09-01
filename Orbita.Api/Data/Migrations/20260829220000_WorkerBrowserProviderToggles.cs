using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260829220000_WorkerBrowserProviderToggles")]
public partial class WorkerBrowserProviderToggles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "AdsPowerEnabled",
            table: "Workers",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "MultiloginEnabled",
            table: "Workers",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<bool>(
            name: "LocalChromeEnabled",
            table: "Workers",
            type: "boolean",
            nullable: false,
            defaultValue: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AdsPowerEnabled",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "MultiloginEnabled",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "LocalChromeEnabled",
            table: "Workers");
    }
}
