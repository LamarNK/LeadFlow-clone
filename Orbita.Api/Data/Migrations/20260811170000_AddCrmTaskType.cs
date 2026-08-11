using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260811170000_AddCrmTaskType")]
public partial class AddCrmTaskType : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "TaskType",
            table: "CrmTasks",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "Unspecified");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TaskType",
            table: "CrmTasks");
    }
}
