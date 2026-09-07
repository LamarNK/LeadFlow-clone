using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260906140000_AddCrmHistoryContext")]
public sealed class AddCrmHistoryContext : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>("EntryOfficeId", "CrmCandidateCards", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>("InitialAssignedOfficeId", "CrmCandidateCards", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("EntryStage", "CrmCandidateCards", type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<Guid>("OfficeId", "CrmCandidateHistory", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>("ResponsibleUserId", "CrmCandidateHistory", type: "character varying(128)", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<string>("PreviousUserId", "CrmCandidateHistory", type: "character varying(128)", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<string>("StageAtEvent", "CrmCandidateHistory", type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>("PreviousCloseReason", "CrmCandidateHistory", type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<bool>("ContextInferred", "CrmCandidateHistory", type: "boolean", nullable: false, defaultValue: false);
        // Freeze the legacy reporting baseline, explicitly marking its uncertainty.
        // Do NOT invent a historical responsible, assignment recipient or initial stage.
        migrationBuilder.Sql("""
            UPDATE "CrmCandidateCards" SET "EntryOfficeId" = "OfficeId" WHERE "EntryOfficeId" IS NULL;
            UPDATE "CrmCandidateCards" SET "InitialAssignedOfficeId" = "OfficeId" WHERE "InitialManagerUserId" IS NOT NULL;
            UPDATE "CrmCandidateHistory" h
            SET "OfficeId" = c."OfficeId", "ContextInferred" = TRUE
            FROM "CrmCandidateCards" c WHERE c."Id" = h."CardId" AND h."OfficeId" IS NULL;
            """);
        migrationBuilder.CreateIndex("IX_CrmCandidateCards_EntryOfficeId_EnteredCrmAtUtc", "CrmCandidateCards", new[] { "EntryOfficeId", "EnteredCrmAtUtc" });
        migrationBuilder.CreateIndex("IX_CrmCandidateHistory_OfficeId_CreatedAtUtc_Action", "CrmCandidateHistory", new[] { "OfficeId", "CreatedAtUtc", "Action" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_CrmCandidateCards_EntryOfficeId_EnteredCrmAtUtc", "CrmCandidateCards");
        migrationBuilder.DropIndex("IX_CrmCandidateHistory_OfficeId_CreatedAtUtc_Action", "CrmCandidateHistory");
        migrationBuilder.DropColumn("EntryOfficeId", "CrmCandidateCards");
        migrationBuilder.DropColumn("InitialAssignedOfficeId", "CrmCandidateCards");
        migrationBuilder.DropColumn("EntryStage", "CrmCandidateCards");
        foreach (var column in new[] { "OfficeId", "ResponsibleUserId", "PreviousUserId", "StageAtEvent", "PreviousCloseReason", "ContextInferred" })
            migrationBuilder.DropColumn(column, "CrmCandidateHistory");
    }
}
