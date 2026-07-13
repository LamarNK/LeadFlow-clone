using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260713120000_CandidateGender")]
public partial class CandidateGender : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Gender",
            table: "CandidateResponses",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "");

        migrationBuilder.Sql(
            """
            UPDATE "CandidateResponses"
            SET "Gender" = CASE
                WHEN "RawText" ILIKE '%мужчина%' THEN 'male'
                WHEN "RawText" ILIKE '%женщина%' THEN 'female'
                ELSE ''
            END
            WHERE "Gender" = '';
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Gender",
            table: "CandidateResponses");
    }
}