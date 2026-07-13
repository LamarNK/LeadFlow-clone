using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260710120000_CandidateResponseCardFingerprint")]
public partial class CandidateResponseCardFingerprint : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CardFingerprint",
            table: "CandidateResponses",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateIndex(
            name: "IX_CandidateResponses_OfficeId_AccountId_AvitoSubProfileId_CardFingerprint",
            table: "CandidateResponses",
            columns: new[] { "OfficeId", "AccountId", "AvitoSubProfileId", "CardFingerprint" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CandidateResponses_OfficeId_AccountId_AvitoSubProfileId_CardFingerprint",
            table: "CandidateResponses");

        migrationBuilder.DropColumn(
            name: "CardFingerprint",
            table: "CandidateResponses");
    }
}