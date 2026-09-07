using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260905160000_AddCrmAnalyticsAttributionFields")]
public partial class AddCrmAnalyticsAttributionFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "EnteredCrmAtUtc",
            table: "CrmCandidateCards",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TargetUserId",
            table: "CrmCandidateHistory",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        // Recover the actual entry moment for existing rows when an Orbita creation
        // event or Bitrix import delivery exists. Truly legacy rows keep their former
        // CreatedAtUtc reporting date instead of moving to migration day.
        migrationBuilder.Sql(
            """
            UPDATE "CrmCandidateCards" AS card
            SET "EnteredCrmAtUtc" = COALESCE(
                (
                    SELECT MIN(history."CreatedAtUtc")
                    FROM "CrmCandidateHistory" AS history
                    WHERE history."CardId" = card."Id"
                      AND history."Action" = 'Created'
                ),
                (
                    SELECT MIN(delivery."CreatedAtUtc")
                    FROM "ResponseBitrixDeliveries" AS delivery
                    WHERE delivery."ResponseId" = card."ResponseId"
                      AND delivery."Source" = 'Import'
                ),
                card."CreatedAtUtc")
            WHERE card."EnteredCrmAtUtc" IS NULL;
            """);

        // Legacy recipients remain unknown. A first surviving history row is not
        // proof of the first assignment; new events record the recipient explicitly.

        migrationBuilder.CreateIndex(
            name: "IX_CrmCandidateCards_OfficeId_EnteredCrmAtUtc",
            table: "CrmCandidateCards",
            columns: new[] { "OfficeId", "EnteredCrmAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CrmCandidateCards_OfficeId_EnteredCrmAtUtc",
            table: "CrmCandidateCards");

        migrationBuilder.DropColumn(
            name: "EnteredCrmAtUtc",
            table: "CrmCandidateCards");

        migrationBuilder.DropColumn(
            name: "TargetUserId",
            table: "CrmCandidateHistory");
    }
}
