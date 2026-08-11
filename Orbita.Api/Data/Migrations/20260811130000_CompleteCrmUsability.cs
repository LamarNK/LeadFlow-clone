using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260811130000_CompleteCrmUsability")]
public partial class CompleteCrmUsability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsPinned",
            table: "CrmCandidateNotes",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTime>(
            name: "UpdatedAtUtc",
            table: "CrmCandidateNotes",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "UpdatedAtUtc",
            table: "CrmTaskComments",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.DropIndex(
            name: "IX_CrmCandidateNotes_CardId_CreatedAtUtc",
            table: "CrmCandidateNotes");

        migrationBuilder.CreateIndex(
            name: "IX_CrmCandidateNotes_CardId_IsPinned_CreatedAtUtc",
            table: "CrmCandidateNotes",
            columns: new[] { "CardId", "IsPinned", "CreatedAtUtc" });

        migrationBuilder.AlterColumn<bool>(
            name: "CrmDeadlineNotificationsEnabled",
            table: "Offices",
            type: "boolean",
            nullable: false,
            defaultValue: true,
            oldClrType: typeof(bool),
            oldType: "boolean",
            oldDefaultValue: false);

        migrationBuilder.Sql(
            """
            UPDATE "Offices"
            SET "CrmDeadlineNotificationsEnabled" = TRUE,
                "CrmDeadlineNotificationsEnabledAtUtc" = COALESCE("CrmDeadlineNotificationsEnabledAtUtc", CURRENT_TIMESTAMP)
            WHERE "CrmDeadlineNotificationsEnabled" = FALSE;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CrmCandidateNotes_CardId_IsPinned_CreatedAtUtc",
            table: "CrmCandidateNotes");

        migrationBuilder.DropColumn(
            name: "IsPinned",
            table: "CrmCandidateNotes");

        migrationBuilder.DropColumn(
            name: "UpdatedAtUtc",
            table: "CrmCandidateNotes");

        migrationBuilder.DropColumn(
            name: "UpdatedAtUtc",
            table: "CrmTaskComments");

        migrationBuilder.CreateIndex(
            name: "IX_CrmCandidateNotes_CardId_CreatedAtUtc",
            table: "CrmCandidateNotes",
            columns: new[] { "CardId", "CreatedAtUtc" });

        migrationBuilder.AlterColumn<bool>(
            name: "CrmDeadlineNotificationsEnabled",
            table: "Offices",
            type: "boolean",
            nullable: false,
            defaultValue: false,
            oldClrType: typeof(bool),
            oldType: "boolean",
            oldDefaultValue: true);
    }
}
