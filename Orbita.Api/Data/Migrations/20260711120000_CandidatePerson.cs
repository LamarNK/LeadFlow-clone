using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

/// <inheritdoc />
public partial class CandidatePerson : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CandidatePersons",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                FullName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                FirstName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                LastName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                MiddleName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Age = table.Column<int>(type: "integer", nullable: true),
                City = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                PhoneRaw = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PhoneNormalized = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CandidatePersons", x => x.Id);
                table.ForeignKey(
                    name: "FK_CandidatePersons_Offices_OfficeId",
                    column: x => x.OfficeId,
                    principalTable: "Offices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "CandidatePhoneHistory",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                ResponseId = table.Column<Guid>(type: "uuid", nullable: true),
                PhoneRaw = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PhoneNormalized = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CandidatePhoneHistory", x => x.Id);
                table.ForeignKey(
                    name: "FK_CandidatePhoneHistory_CandidatePersons_PersonId",
                    column: x => x.PersonId,
                    principalTable: "CandidatePersons",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.AddColumn<Guid>(
            name: "PersonId",
            table: "CandidateResponses",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql(
            """
            INSERT INTO "CandidatePersons" (
                "Id", "OfficeId", "FullName", "FirstName", "LastName", "MiddleName",
                "Age", "City", "PhoneRaw", "PhoneNormalized", "CreatedAtUtc", "UpdatedAtUtc")
            SELECT
                gen_random_uuid(),
                grouped."OfficeId",
                grouped."FullName",
                grouped."FirstName",
                grouped."LastName",
                grouped."MiddleName",
                grouped."Age",
                grouped."City",
                grouped."PhoneRaw",
                grouped."PhoneNormalized",
                grouped."CreatedAt",
                grouped."CreatedAt"
            FROM (
                SELECT DISTINCT ON (
                    r."OfficeId",
                    lower(r."LastName"),
                    lower(r."FirstName"),
                    lower(r."MiddleName"),
                    r."PhoneNormalized")
                    r."OfficeId",
                    r."FullName",
                    r."FirstName",
                    r."LastName",
                    r."MiddleName",
                    r."Age",
                    r."City",
                    r."PhoneRaw",
                    r."PhoneNormalized",
                    r."CreatedAt"
                FROM "CandidateResponses" r
                ORDER BY
                    r."OfficeId",
                    lower(r."LastName"),
                    lower(r."FirstName"),
                    lower(r."MiddleName"),
                    r."PhoneNormalized",
                    r."CreatedAt"
            ) grouped;

            UPDATE "CandidateResponses" r
            SET "PersonId" = p."Id"
            FROM "CandidatePersons" p
            WHERE p."OfficeId" = r."OfficeId"
              AND lower(p."LastName") = lower(r."LastName")
              AND lower(p."FirstName") = lower(r."FirstName")
              AND lower(p."MiddleName") = lower(r."MiddleName")
              AND p."PhoneNormalized" = r."PhoneNormalized"
              AND r."PersonId" IS NULL;
            """);

        migrationBuilder.AlterColumn<Guid>(
            name: "PersonId",
            table: "CandidateResponses",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_CandidatePersons_OfficeId",
            table: "CandidatePersons",
            column: "OfficeId");

        migrationBuilder.CreateIndex(
            name: "IX_CandidatePersons_OfficeId_LastName_FirstName_MiddleName",
            table: "CandidatePersons",
            columns: new[] { "OfficeId", "LastName", "FirstName", "MiddleName" });

        migrationBuilder.CreateIndex(
            name: "IX_CandidatePhoneHistory_PersonId",
            table: "CandidatePhoneHistory",
            column: "PersonId");

        migrationBuilder.CreateIndex(
            name: "IX_CandidatePhoneHistory_PersonId_RecordedAtUtc",
            table: "CandidatePhoneHistory",
            columns: new[] { "PersonId", "RecordedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CandidateResponses_PersonId",
            table: "CandidateResponses",
            column: "PersonId");

        migrationBuilder.AddForeignKey(
            name: "FK_CandidatePhoneHistory_CandidateResponses_ResponseId",
            table: "CandidatePhoneHistory",
            column: "ResponseId",
            principalTable: "CandidateResponses",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_CandidateResponses_CandidatePersons_PersonId",
            table: "CandidateResponses",
            column: "PersonId",
            principalTable: "CandidatePersons",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_CandidateResponses_CandidatePersons_PersonId",
            table: "CandidateResponses");

        migrationBuilder.DropTable(
            name: "CandidatePhoneHistory");

        migrationBuilder.DropTable(
            name: "CandidatePersons");

        migrationBuilder.DropIndex(
            name: "IX_CandidateResponses_PersonId",
            table: "CandidateResponses");

        migrationBuilder.DropColumn(
            name: "PersonId",
            table: "CandidateResponses");
    }
}