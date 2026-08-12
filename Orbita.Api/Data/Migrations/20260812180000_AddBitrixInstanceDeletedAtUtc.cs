using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260812180000_AddBitrixInstanceDeletedAtUtc")]
public partial class AddBitrixInstanceDeletedAtUtc : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "DeletedAtUtc",
            table: "BitrixInstances",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_BitrixInstances_OfficeId_DeletedAtUtc",
            table: "BitrixInstances",
            columns: new[] { "OfficeId", "DeletedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_BitrixInstances_OfficeId_DeletedAtUtc",
            table: "BitrixInstances");

        migrationBuilder.DropColumn(
            name: "DeletedAtUtc",
            table: "BitrixInstances");
    }
}
