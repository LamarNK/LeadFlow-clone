using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260901120000_WorkerAccountLocalTraffic")]
public partial class WorkerAccountLocalTraffic : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LocalTrafficMode",
            table: "WorkerAccounts",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Normal");

        migrationBuilder.AddColumn<bool>(
            name: "LocalBlockMedia",
            table: "WorkerAccounts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "LocalBlockAnalytics",
            table: "WorkerAccounts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "LocalBlockImages",
            table: "WorkerAccounts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "LocalBlockFonts",
            table: "WorkerAccounts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "LocalBlockPrefetch",
            table: "WorkerAccounts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "LocalNavigationTimeoutSeconds",
            table: "WorkerAccounts",
            type: "integer",
            nullable: false,
            defaultValue: 60);

        migrationBuilder.AddColumn<int>(
            name: "LocalTrafficLastNavigationMs",
            table: "WorkerAccounts",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "LocalTrafficBlockedMedia",
            table: "WorkerAccounts",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "LocalTrafficBlockedImages",
            table: "WorkerAccounts",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "LocalTrafficBlockedFonts",
            table: "WorkerAccounts",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "LocalTrafficBlockedAnalytics",
            table: "WorkerAccounts",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "LocalTrafficBlockedPrefetch",
            table: "WorkerAccounts",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LocalTrafficMode", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalBlockMedia", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalBlockAnalytics", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalBlockImages", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalBlockFonts", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalBlockPrefetch", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalNavigationTimeoutSeconds", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalTrafficLastNavigationMs", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalTrafficBlockedMedia", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalTrafficBlockedImages", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalTrafficBlockedFonts", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalTrafficBlockedAnalytics", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalTrafficBlockedPrefetch", table: "WorkerAccounts");
    }
}
