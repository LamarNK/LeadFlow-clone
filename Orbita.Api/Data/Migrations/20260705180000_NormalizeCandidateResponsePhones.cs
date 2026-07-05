using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260705180000_NormalizeCandidateResponsePhones")]
public partial class NormalizeCandidateResponsePhones : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "CandidateResponses"
            SET "PhoneNormalized" = '7' || substring("PhoneNormalized" from 2)
            WHERE length("PhoneNormalized") = 11
              AND left("PhoneNormalized", 1) = '8'
              AND "PhoneNormalized" ~ '^\d{11}$';
            """);

        migrationBuilder.Sql(
            """
            UPDATE "CandidateResponses"
            SET "PhoneNormalized" = '7' || "PhoneNormalized"
            WHERE length("PhoneNormalized") = 10
              AND "PhoneNormalized" ~ '^\d{10}$';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}