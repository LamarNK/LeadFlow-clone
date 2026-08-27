using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260826193000_AddCrmInitialManagerAttribution")]
public partial class AddCrmInitialManagerAttribution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "InitialAssignedAtUtc",
            table: "CrmCandidateCards",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "InitialManagerUserId",
            table: "CrmCandidateCards",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE "CrmCandidateCards"
            SET "InitialManagerUserId" = "ManagerUserId",
                "InitialAssignedAtUtc" = COALESCE(
                    (
                        SELECT MIN(history."CreatedAtUtc")
                        FROM "CrmCandidateHistory" AS history
                        WHERE history."CardId" = "CrmCandidateCards"."Id"
                          AND history."Action" = 'Assigned'
                    ),
                    "CreatedAtUtc")
            WHERE "InitialManagerUserId" IS NULL
              AND "ManagerUserId" IS NOT NULL
              AND "ManagerUserId" <> '';
            """);

        migrationBuilder.CreateIndex(
            name: "IX_CrmCards_Office_InitialManager_AssignedAt",
            table: "CrmCandidateCards",
            columns: new[] { "OfficeId", "InitialManagerUserId", "InitialAssignedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CrmHistory_Action_CreatedAt_Card",
            table: "CrmCandidateHistory",
            columns: new[] { "Action", "CreatedAtUtc", "CardId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CrmHistory_Action_CreatedAt_Card",
            table: "CrmCandidateHistory");

        migrationBuilder.DropIndex(
            name: "IX_CrmCards_Office_InitialManager_AssignedAt",
            table: "CrmCandidateCards");

        migrationBuilder.DropColumn(
            name: "InitialAssignedAtUtc",
            table: "CrmCandidateCards");

        migrationBuilder.DropColumn(
            name: "InitialManagerUserId",
            table: "CrmCandidateCards");
    }
}
