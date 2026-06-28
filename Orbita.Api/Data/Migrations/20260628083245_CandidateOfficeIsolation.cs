using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CandidateOfficeIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CandidateResponses_AccountId_SourceResponseId_PhoneNormaliz~",
                table: "CandidateResponses");

            migrationBuilder.AddColumn<string>(
                name: "DuplicateSummary",
                table: "CandidateResponses",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsBitrixDuplicate",
                table: "CandidateResponses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsLocalDuplicate",
                table: "CandidateResponses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OfficeId",
                table: "CandidateResponses",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "CandidateResponses" AS cr
                SET "OfficeId" = w."OfficeId"
                FROM "Workers" AS w
                WHERE cr."WorkerId" = w."Id" AND cr."OfficeId" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "CandidateResponses"
                SET "OfficeId" = '11111111-1111-1111-1111-111111111111'
                WHERE "OfficeId" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "OfficeId",
                table: "CandidateResponses",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidateResponses_AccountId_SourceResponseId",
                table: "CandidateResponses",
                columns: new[] { "AccountId", "SourceResponseId" },
                unique: true,
                filter: "\"SourceResponseId\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateResponses_OfficeId_CreatedAt",
                table: "CandidateResponses",
                columns: new[] { "OfficeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateResponses_OfficeId_PhoneNormalized",
                table: "CandidateResponses",
                columns: new[] { "OfficeId", "PhoneNormalized" });

            migrationBuilder.AddForeignKey(
                name: "FK_CandidateResponses_Offices_OfficeId",
                table: "CandidateResponses",
                column: "OfficeId",
                principalTable: "Offices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CandidateResponses_Offices_OfficeId",
                table: "CandidateResponses");

            migrationBuilder.DropIndex(
                name: "IX_CandidateResponses_AccountId_SourceResponseId",
                table: "CandidateResponses");

            migrationBuilder.DropIndex(
                name: "IX_CandidateResponses_OfficeId_CreatedAt",
                table: "CandidateResponses");

            migrationBuilder.DropIndex(
                name: "IX_CandidateResponses_OfficeId_PhoneNormalized",
                table: "CandidateResponses");

            migrationBuilder.DropColumn(
                name: "DuplicateSummary",
                table: "CandidateResponses");

            migrationBuilder.DropColumn(
                name: "IsBitrixDuplicate",
                table: "CandidateResponses");

            migrationBuilder.DropColumn(
                name: "IsLocalDuplicate",
                table: "CandidateResponses");

            migrationBuilder.DropColumn(
                name: "OfficeId",
                table: "CandidateResponses");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateResponses_AccountId_SourceResponseId_PhoneNormaliz~",
                table: "CandidateResponses",
                columns: new[] { "AccountId", "SourceResponseId", "PhoneNormalized" },
                unique: true);
        }
    }
}
