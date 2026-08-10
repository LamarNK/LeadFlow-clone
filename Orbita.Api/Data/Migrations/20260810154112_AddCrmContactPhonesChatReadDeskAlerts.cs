using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCrmContactPhonesChatReadDeskAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CandidateContactPhones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    PhoneRaw = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PhoneNormalized = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateContactPhones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CandidateContactPhones_CandidatePersons_PersonId",
                        column: x => x.PersonId,
                        principalTable: "CandidatePersons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CrmCardChatReads",
                columns: table => new
                {
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmCardChatReads", x => new { x.CardId, x.UserId });
                    table.ForeignKey(
                        name: "FK_CrmCardChatReads_CrmCandidateCards_CardId",
                        column: x => x.CardId,
                        principalTable: "CrmCandidateCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CrmDeskAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmDeskAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrmDeskAlerts_CrmCandidateCards_CardId",
                        column: x => x.CardId,
                        principalTable: "CrmCandidateCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CrmDeskAlerts_Offices_OfficeId",
                        column: x => x.OfficeId,
                        principalTable: "Offices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateContactPhones_PersonId",
                table: "CandidateContactPhones",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateContactPhones_PersonId_PhoneNormalized",
                table: "CandidateContactPhones",
                columns: new[] { "PersonId", "PhoneNormalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CrmDeskAlerts_CardId",
                table: "CrmDeskAlerts",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmDeskAlerts_OfficeId_RecipientUserId_ReadAtUtc_CreatedAtU~",
                table: "CrmDeskAlerts",
                columns: new[] { "OfficeId", "RecipientUserId", "ReadAtUtc", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CandidateContactPhones");

            migrationBuilder.DropTable(
                name: "CrmCardChatReads");

            migrationBuilder.DropTable(
                name: "CrmDeskAlerts");
        }
    }
}
