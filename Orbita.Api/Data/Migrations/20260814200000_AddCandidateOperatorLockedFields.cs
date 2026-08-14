using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260814200000_AddCandidateOperatorLockedFields")]
public partial class AddCandidateOperatorLockedFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OperatorLockedFields",
            table: "CandidateResponses",
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "OperatorLockedFields",
            table: "CandidateResponses");
    }
}
