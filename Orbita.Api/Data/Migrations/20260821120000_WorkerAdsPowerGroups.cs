using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260821120000_WorkerAdsPowerGroups")]
public partial class WorkerAdsPowerGroups : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AdsPowerGroupId",
            table: "Workers",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AdsPowerGroupName",
            table: "Workers",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AdsPowerGroupsJson",
            table: "Workers",
            type: "character varying(16000)",
            maxLength: 16000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AdsPowerGroupId",
            table: "WorkerAccounts",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AdsPowerGroupName",
            table: "WorkerAccounts",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AdsPowerGroupId",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "AdsPowerGroupName",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "AdsPowerGroupsJson",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "AdsPowerGroupId",
            table: "WorkerAccounts");

        migrationBuilder.DropColumn(
            name: "AdsPowerGroupName",
            table: "WorkerAccounts");
    }
}
