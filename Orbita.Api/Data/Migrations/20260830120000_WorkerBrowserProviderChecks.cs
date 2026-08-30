using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260830120000_WorkerBrowserProviderChecks")]
public partial class WorkerBrowserProviderChecks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BrowserProviderChecksJson",
            table: "Workers",
            type: "character varying(4000)",
            maxLength: 4000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PendingBrowserProviderCheck",
            table: "Workers",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "PendingBrowserProviderCheckAtUtc",
            table: "Workers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PendingBrowserProviderSync",
            table: "Workers",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "PendingBrowserProviderSyncAtUtc",
            table: "Workers",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "BrowserProviderChecksJson", table: "Workers");
        migrationBuilder.DropColumn(name: "PendingBrowserProviderCheck", table: "Workers");
        migrationBuilder.DropColumn(name: "PendingBrowserProviderCheckAtUtc", table: "Workers");
        migrationBuilder.DropColumn(name: "PendingBrowserProviderSync", table: "Workers");
        migrationBuilder.DropColumn(name: "PendingBrowserProviderSyncAtUtc", table: "Workers");
    }
}
