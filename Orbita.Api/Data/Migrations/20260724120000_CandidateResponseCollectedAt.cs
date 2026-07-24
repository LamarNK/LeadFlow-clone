using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260724120000_CandidateResponseCollectedAt")]
public partial class CandidateResponseCollectedAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "CollectedAt",
            table: "CandidateResponses",
            type: "timestamp with time zone",
            nullable: false,
            defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

        migrationBuilder.Sql(
            """
            UPDATE "CandidateResponses"
            SET "CollectedAt" = "CreatedAt";
            """);

        migrationBuilder.CreateIndex(
            name: "IX_CandidateResponses_CollectedAt",
            table: "CandidateResponses",
            column: "CollectedAt");

        migrationBuilder.CreateIndex(
            name: "IX_CandidateResponses_OfficeId_CollectedAt",
            table: "CandidateResponses",
            columns: new[] { "OfficeId", "CollectedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CandidateResponses_OfficeId_CollectedAt",
            table: "CandidateResponses");

        migrationBuilder.DropIndex(
            name: "IX_CandidateResponses_CollectedAt",
            table: "CandidateResponses");

        migrationBuilder.DropColumn(
            name: "CollectedAt",
            table: "CandidateResponses");
    }
}
