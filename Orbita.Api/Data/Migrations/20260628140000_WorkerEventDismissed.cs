using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

public partial class WorkerEventDismissed : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsDismissed",
            table: "WorkerEvents",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime>(
            name: "DismissedAtUtc",
            table: "WorkerEvents",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "IsDismissed", table: "WorkerEvents");
        migrationBuilder.DropColumn(name: "DismissedAtUtc", table: "WorkerEvents");
    }
}