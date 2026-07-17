using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260717120000_WorkerResponseCollectionFilters")]
public partial class WorkerResponseCollectionFilters : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "ResponseFilterEnabled",
            table: "Workers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "ResponseFilterExcludeFemale",
            table: "Workers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "ResponseFilterMaxAge",
            table: "Workers",
            type: "integer",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ResponseFilterEnabled",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "ResponseFilterExcludeFemale",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "ResponseFilterMaxAge",
            table: "Workers");
    }
}
