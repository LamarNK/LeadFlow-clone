using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CrmWorkspaceV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CrmRequireStageComment",
                table: "Offices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CloseReason",
                table: "CrmCandidateCards",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAtUtc",
                table: "CrmCandidateCards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsClosed",
                table: "CrmCandidateCards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastContactAtUtc",
                table: "CrmCandidateCards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextActionAtUtc",
                table: "CrmCandidateCards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StageChangedAtUtc",
                table: "CrmCandidateCards",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.Sql("""
                UPDATE "CrmCandidateCards"
                SET "StageChangedAtUtc" = COALESCE("UpdatedAtUtc", "CreatedAtUtc", NOW() AT TIME ZONE 'utc')
                WHERE "StageChangedAtUtc" < TIMESTAMP '1970-01-01';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CrmCandidateCards_OfficeId_IsClosed_NextActionAtUtc",
                table: "CrmCandidateCards",
                columns: new[] { "OfficeId", "IsClosed", "NextActionAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CrmCandidateCards_OfficeId_IsClosed_NextActionAtUtc",
                table: "CrmCandidateCards");

            migrationBuilder.DropColumn(
                name: "CrmRequireStageComment",
                table: "Offices");

            migrationBuilder.DropColumn(
                name: "CloseReason",
                table: "CrmCandidateCards");

            migrationBuilder.DropColumn(
                name: "ClosedAtUtc",
                table: "CrmCandidateCards");

            migrationBuilder.DropColumn(
                name: "IsClosed",
                table: "CrmCandidateCards");

            migrationBuilder.DropColumn(
                name: "LastContactAtUtc",
                table: "CrmCandidateCards");

            migrationBuilder.DropColumn(
                name: "NextActionAtUtc",
                table: "CrmCandidateCards");

            migrationBuilder.DropColumn(
                name: "StageChangedAtUtc",
                table: "CrmCandidateCards");
        }
    }
}
