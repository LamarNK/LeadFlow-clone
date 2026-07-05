using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260705160000_CandidateResponseWorkerName")]
public partial class CandidateResponseWorkerName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "WorkerName",
            table: "CandidateResponses",
            type: "character varying(200)",
            maxLength: 200,
            nullable: false,
            defaultValue: "");

        migrationBuilder.Sql(
            """
            UPDATE "CandidateResponses" AS cr
            SET "WorkerName" = w."DisplayName"
            FROM "Workers" AS w
            WHERE cr."WorkerId" = w."Id"
              AND cr."WorkerName" = '';
            """);

        migrationBuilder.DropForeignKey(
            name: "FK_CandidateResponses_Workers_WorkerId",
            table: "CandidateResponses");

        migrationBuilder.AlterColumn<Guid>(
            name: "WorkerId",
            table: "CandidateResponses",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");

        migrationBuilder.AddForeignKey(
            name: "FK_CandidateResponses_Workers_WorkerId",
            table: "CandidateResponses",
            column: "WorkerId",
            principalTable: "Workers",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_CandidateResponses_Workers_WorkerId",
            table: "CandidateResponses");

        migrationBuilder.AlterColumn<Guid>(
            name: "WorkerId",
            table: "CandidateResponses",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.DropColumn(
            name: "WorkerName",
            table: "CandidateResponses");

        migrationBuilder.AddForeignKey(
            name: "FK_CandidateResponses_Workers_WorkerId",
            table: "CandidateResponses",
            column: "WorkerId",
            principalTable: "Workers",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }
}