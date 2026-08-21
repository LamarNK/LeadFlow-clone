using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260821180000_AddMonitoringPassCollectedAndCaptcha")]
public partial class AddMonitoringPassCollectedAndCaptcha : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CollectedCount",
            table: "MonitoringSubProfileRuns",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "CaptchaCount",
            table: "MonitoringSubProfileRuns",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "CaptchaSolvedCount",
            table: "MonitoringSubProfileRuns",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CollectedCount",
            table: "MonitoringSubProfileRuns");

        migrationBuilder.DropColumn(
            name: "CaptchaCount",
            table: "MonitoringSubProfileRuns");

        migrationBuilder.DropColumn(
            name: "CaptchaSolvedCount",
            table: "MonitoringSubProfileRuns");
    }
}
