using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260704130000_CandidateResponseSubProfileName")]
public partial class CandidateResponseSubProfileName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AvitoSubProfileName",
            table: "CandidateResponses",
            type: "text",
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AvitoSubProfileName",
            table: "CandidateResponses");
    }
}