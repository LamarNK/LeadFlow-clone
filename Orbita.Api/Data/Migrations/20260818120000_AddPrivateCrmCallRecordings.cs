using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260818120000_AddPrivateCrmCallRecordings")]
public partial class AddPrivateCrmCallRecordings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RecordingContentType",
            table: "CrmCalls",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "RecordingFileName",
            table: "CrmCalls",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "RecordingStoragePath",
            table: "CrmCalls",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RecordingContentType", table: "CrmCalls");
        migrationBuilder.DropColumn(name: "RecordingFileName", table: "CrmCalls");
        migrationBuilder.DropColumn(name: "RecordingStoragePath", table: "CrmCalls");
    }
}
