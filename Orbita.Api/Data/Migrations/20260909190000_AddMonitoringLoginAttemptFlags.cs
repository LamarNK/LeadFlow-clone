using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260909190000_AddMonitoringLoginAttemptFlags")]
public sealed class AddMonitoringLoginAttemptFlags : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "LoginAttempted",
            table: "MonitoringSubProfileRuns",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "LoginSucceeded",
            table: "MonitoringSubProfileRuns",
            type: "boolean",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "LoginAttempted",
            table: "MonitoringSubProfileRuns");

        migrationBuilder.DropColumn(
            name: "LoginSucceeded",
            table: "MonitoringSubProfileRuns");
    }
}
