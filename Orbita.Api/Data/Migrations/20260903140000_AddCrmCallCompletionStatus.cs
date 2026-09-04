using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260903140000_AddCrmCallCompletionStatus")]
public partial class AddCrmCallCompletionStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DialStatus",
            table: "CrmCalls",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Disposition",
            table: "CrmCalls",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "HangupCause",
            table: "CrmCalls",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Status",
            table: "CrmCalls",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "unknown");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "DialStatus", table: "CrmCalls");
        migrationBuilder.DropColumn(name: "Disposition", table: "CrmCalls");
        migrationBuilder.DropColumn(name: "HangupCause", table: "CrmCalls");
        migrationBuilder.DropColumn(name: "Status", table: "CrmCalls");
    }
}
