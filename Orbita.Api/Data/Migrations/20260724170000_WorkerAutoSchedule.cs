using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260724170000_WorkerAutoSchedule")]
public partial class WorkerAutoSchedule : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AutoScheduleDays",
            table: "Workers",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "AutoScheduleEnabled",
            table: "Workers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "AutoScheduleFromLocalTime",
            table: "Workers",
            type: "character varying(5)",
            maxLength: 5,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AutoScheduleToLocalTime",
            table: "Workers",
            type: "character varying(5)",
            maxLength: 5,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AutoScheduleDays", table: "Workers");
        migrationBuilder.DropColumn(name: "AutoScheduleEnabled", table: "Workers");
        migrationBuilder.DropColumn(name: "AutoScheduleFromLocalTime", table: "Workers");
        migrationBuilder.DropColumn(name: "AutoScheduleToLocalTime", table: "Workers");
    }
}
