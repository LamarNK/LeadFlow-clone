using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260709140000_ResponseBitrixDeliveries")]
public partial class ResponseBitrixDeliveries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ResponseBitrixDeliveries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ResponseId = table.Column<Guid>(type: "uuid", nullable: false),
                BitrixInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                BitrixEntityId = table.Column<string>(type: "text", nullable: false),
                BitrixEntityType = table.Column<string>(type: "text", nullable: false),
                BitrixContactId = table.Column<string>(type: "text", nullable: false),
                ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ResponseBitrixDeliveries", x => x.Id);
                table.ForeignKey(
                    name: "FK_ResponseBitrixDeliveries_BitrixInstances_BitrixInstanceId",
                    column: x => x.BitrixInstanceId,
                    principalTable: "BitrixInstances",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ResponseBitrixDeliveries_CandidateResponses_ResponseId",
                    column: x => x.ResponseId,
                    principalTable: "CandidateResponses",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ResponseBitrixDeliveries_BitrixInstanceId",
            table: "ResponseBitrixDeliveries",
            column: "BitrixInstanceId");

        migrationBuilder.CreateIndex(
            name: "IX_ResponseBitrixDeliveries_ResponseId",
            table: "ResponseBitrixDeliveries",
            column: "ResponseId");

        migrationBuilder.CreateIndex(
            name: "IX_ResponseBitrixDeliveries_ResponseId_CreatedAtUtc",
            table: "ResponseBitrixDeliveries",
            columns: new[] { "ResponseId", "CreatedAtUtc" });

        migrationBuilder.Sql("""
            INSERT INTO "ResponseBitrixDeliveries" (
                "Id", "ResponseId", "BitrixInstanceId", "Outcome", "BitrixEntityId", "BitrixEntityType",
                "BitrixContactId", "ErrorMessage", "Source", "CreatedAtUtc")
            SELECT
                gen_random_uuid(),
                cr."Id",
                cr."BitrixInstanceId",
                'Sent',
                cr."BitrixEntityId",
                cr."BitrixEntityType",
                cr."BitrixContactId",
                '',
                CASE
                    WHEN cr."DistributionMode" IN ('manual', 'broadcast', 'chain_fallback', 'auto') THEN cr."DistributionMode"
                    ELSE 'legacy'
                END,
                COALESCE(cr."ProcessedAt", cr."CreatedAt")
            FROM "CandidateResponses" cr
            WHERE cr."BitrixInstanceId" IS NOT NULL
              AND cr."Status" = 'Sent';
            """);

        migrationBuilder.Sql("""
            INSERT INTO "ResponseBitrixDeliveries" (
                "Id", "ResponseId", "BitrixInstanceId", "Outcome", "BitrixEntityId", "BitrixEntityType",
                "BitrixContactId", "ErrorMessage", "Source", "CreatedAtUtc")
            SELECT
                gen_random_uuid(),
                cr."Id",
                cr."DuplicateBitrixInstanceId",
                'Duplicate',
                '',
                '',
                '',
                COALESCE(NULLIF(cr."DuplicateSummary", ''), 'Дубль в Битриксе'),
                CASE
                    WHEN cr."DistributionMode" IN ('manual', 'broadcast', 'chain_fallback', 'auto') THEN cr."DistributionMode"
                    ELSE 'legacy'
                END,
                COALESCE(cr."ProcessedAt", cr."CreatedAt")
            FROM "CandidateResponses" cr
            WHERE cr."DuplicateBitrixInstanceId" IS NOT NULL
              AND cr."IsBitrixDuplicate" = TRUE;
            """);

        migrationBuilder.Sql("""
            INSERT INTO "ResponseBitrixDeliveries" (
                "Id", "ResponseId", "BitrixInstanceId", "Outcome", "BitrixEntityId", "BitrixEntityType",
                "BitrixContactId", "ErrorMessage", "Source", "CreatedAtUtc")
            SELECT
                gen_random_uuid(),
                cr."Id",
                cr."BitrixInstanceId",
                'Error',
                '',
                cr."BitrixEntityType",
                cr."BitrixContactId",
                COALESCE(NULLIF(cr."ErrorMessage", ''), 'Ошибка отправки в Bitrix24'),
                CASE
                    WHEN cr."DistributionMode" IN ('manual', 'broadcast', 'chain_fallback', 'auto') THEN cr."DistributionMode"
                    ELSE 'legacy'
                END,
                COALESCE(cr."ProcessedAt", cr."CreatedAt")
            FROM "CandidateResponses" cr
            WHERE cr."BitrixInstanceId" IS NOT NULL
              AND cr."Status" = 'Error';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ResponseBitrixDeliveries");
    }
}