using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260724150000_WorkerResponseHighlightSettings")]
public partial class WorkerResponseHighlightSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ResponseHighlightAgeBuckets",
            table: "Workers",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "ResponseHighlightEnabled",
            table: "Workers",
            type: "boolean",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ResponseHighlightAgeBuckets",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "ResponseHighlightEnabled",
            table: "Workers");
    }
}
