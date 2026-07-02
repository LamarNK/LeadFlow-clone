using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260702120000_WorkerActivity")]
public partial class WorkerActivity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ActivityAccountId",
            table: "Workers",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActivityAccountName",
            table: "Workers",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "ActivityNextCycleAtUtc",
            table: "Workers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActivityMessage",
            table: "Workers",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActivityPhase",
            table: "Workers",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActivitySubProfileId",
            table: "Workers",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ActivitySubProfileName",
            table: "Workers",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "ActivityUpdatedAtUtc",
            table: "Workers",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ActivityAccountId", table: "Workers");
        migrationBuilder.DropColumn(name: "ActivityAccountName", table: "Workers");
        migrationBuilder.DropColumn(name: "ActivityNextCycleAtUtc", table: "Workers");
        migrationBuilder.DropColumn(name: "ActivityMessage", table: "Workers");
        migrationBuilder.DropColumn(name: "ActivityPhase", table: "Workers");
        migrationBuilder.DropColumn(name: "ActivitySubProfileId", table: "Workers");
        migrationBuilder.DropColumn(name: "ActivitySubProfileName", table: "Workers");
        migrationBuilder.DropColumn(name: "ActivityUpdatedAtUtc", table: "Workers");
    }
}