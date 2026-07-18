using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260718120000_WorkerResponseFiltersPerGender")]
public partial class WorkerResponseFiltersPerGender : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "ResponseFilterExcludeMale",
            table: "Workers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "ResponseFilterMaxAgeMale",
            table: "Workers",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ResponseFilterMaxAgeFemale",
            table: "Workers",
            type: "integer",
            nullable: true);

        // Переносим старый единый лимит в оба пола, чтобы поведение не изменилось.
        migrationBuilder.Sql(
            """
            UPDATE "Workers"
            SET
                "ResponseFilterMaxAgeMale" = "ResponseFilterMaxAge",
                "ResponseFilterMaxAgeFemale" = "ResponseFilterMaxAge"
            WHERE "ResponseFilterMaxAge" IS NOT NULL;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ResponseFilterExcludeMale",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "ResponseFilterMaxAgeMale",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "ResponseFilterMaxAgeFemale",
            table: "Workers");
    }
}
