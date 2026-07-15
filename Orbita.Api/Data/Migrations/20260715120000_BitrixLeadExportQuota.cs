using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260715120000_BitrixLeadExportQuota")]
public partial class BitrixLeadExportQuota : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "LeadExportLimit",
            table: "BitrixInstances",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "LeadExportSessionCount",
            table: "BitrixInstances",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "LeadExportSessionStartedAtUtc",
            table: "BitrixInstances",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LeadExportLimit", table: "BitrixInstances");
        migrationBuilder.DropColumn(name: "LeadExportSessionCount", table: "BitrixInstances");
        migrationBuilder.DropColumn(name: "LeadExportSessionStartedAtUtc", table: "BitrixInstances");
    }
}