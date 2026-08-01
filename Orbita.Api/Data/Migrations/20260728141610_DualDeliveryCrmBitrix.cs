using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DualDeliveryCrmBitrix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CandidatePersons_Offices_OfficeId",
                table: "CandidatePersons");

            migrationBuilder.DropForeignKey(
                name: "FK_CandidateResponses_Offices_OfficeId",
                table: "CandidateResponses");

            migrationBuilder.DropIndex(
                name: "IX_CandidatePersons_OfficeId_LastName_FirstName_MiddleName",
                table: "CandidatePersons");

            migrationBuilder.AddColumn<bool>(
                name: "AutoDeliverToBitrix",
                table: "Workers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoDeliverToCrm",
                table: "Workers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "OfficeId",
                table: "CandidateResponses",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "OfficeId",
                table: "CandidatePersons",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "ResponseCrmDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponseId = table.Column<Guid>(type: "uuid", nullable: false),
                    OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResponseCrmDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResponseCrmDeliveries_CandidateResponses_ResponseId",
                        column: x => x.ResponseId,
                        principalTable: "CandidateResponses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResponseCrmDeliveries_CrmCandidateCards_CardId",
                        column: x => x.CardId,
                        principalTable: "CrmCandidateCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ResponseCrmDeliveries_Offices_OfficeId",
                        column: x => x.OfficeId,
                        principalTable: "Offices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CandidatePersons_LastName_FirstName_MiddleName",
                table: "CandidatePersons",
                columns: new[] { "LastName", "FirstName", "MiddleName" });

            migrationBuilder.CreateIndex(
                name: "IX_ResponseCrmDeliveries_CardId",
                table: "ResponseCrmDeliveries",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_ResponseCrmDeliveries_OfficeId",
                table: "ResponseCrmDeliveries",
                column: "OfficeId");

            migrationBuilder.CreateIndex(
                name: "IX_ResponseCrmDeliveries_ResponseId",
                table: "ResponseCrmDeliveries",
                column: "ResponseId");

            migrationBuilder.CreateIndex(
                name: "IX_ResponseCrmDeliveries_ResponseId_CreatedAtUtc",
                table: "ResponseCrmDeliveries",
                columns: new[] { "ResponseId", "CreatedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_CandidatePersons_Offices_OfficeId",
                table: "CandidatePersons",
                column: "OfficeId",
                principalTable: "Offices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_CandidateResponses_Offices_OfficeId",
                table: "CandidateResponses",
                column: "OfficeId",
                principalTable: "Offices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Preserve previous auto-Bitrix behaviour from office distribution routes.
            migrationBuilder.Sql(
                """
                UPDATE "Workers" w
                SET "AutoDeliverToBitrix" = COALESCE(r."IsAutoDistributionEnabled", o."BitrixTransmissionEnabled", TRUE)
                FROM "Offices" o
                LEFT JOIN "DistributionRoutes" r ON r."OfficeId" = o."Id"
                WHERE w."OfficeId" = o."Id";
                """);

            // Enable auto-CRM for workers of offices that already had CRM pilot on.
            migrationBuilder.Sql(
                """
                UPDATE "Workers" w
                SET "AutoDeliverToCrm" = TRUE
                FROM "Offices" o
                WHERE w."OfficeId" = o."Id"
                  AND o."CrmEnabled" = TRUE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CandidatePersons_Offices_OfficeId",
                table: "CandidatePersons");

            migrationBuilder.DropForeignKey(
                name: "FK_CandidateResponses_Offices_OfficeId",
                table: "CandidateResponses");

            migrationBuilder.DropTable(
                name: "ResponseCrmDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_CandidatePersons_LastName_FirstName_MiddleName",
                table: "CandidatePersons");

            migrationBuilder.DropColumn(
                name: "AutoDeliverToBitrix",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "AutoDeliverToCrm",
                table: "Workers");

            migrationBuilder.AlterColumn<Guid>(
                name: "OfficeId",
                table: "CandidateResponses",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "OfficeId",
                table: "CandidatePersons",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidatePersons_OfficeId_LastName_FirstName_MiddleName",
                table: "CandidatePersons",
                columns: new[] { "OfficeId", "LastName", "FirstName", "MiddleName" });

            migrationBuilder.AddForeignKey(
                name: "FK_CandidatePersons_Offices_OfficeId",
                table: "CandidatePersons",
                column: "OfficeId",
                principalTable: "Offices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CandidateResponses_Offices_OfficeId",
                table: "CandidateResponses",
                column: "OfficeId",
                principalTable: "Offices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
