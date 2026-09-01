using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260829120000_WorkerLocalChrome")]
public partial class WorkerLocalChrome : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LocalChromeExecutablePath",
            table: "Workers",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LocalUserDataDir",
            table: "WorkerAccounts",
            type: "character varying(1024)",
            maxLength: 1024,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LocalChromeExecutablePath",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "LocalUserDataDir",
            table: "WorkerAccounts");
    }
}
