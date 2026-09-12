using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerAvitoAds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkerAvitoAds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AvitoSubProfileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AvitoItemId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    StatusText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PublishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PublicationDateSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AgeDays = table.Column<int>(type: "integer", nullable: true),
                    RemainingDays = table.Column<int>(type: "integer", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DetailCheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessfulListCheckAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastParseError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerAvitoAds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkerAvitoAds_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerAvitoAds_State_ExpiresAtUtc",
                table: "WorkerAvitoAds",
                columns: new[] { "State", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerAvitoAds_WorkerId_AccountId_AvitoSubProfileId_AvitoIt~",
                table: "WorkerAvitoAds",
                columns: new[] { "WorkerId", "AccountId", "AvitoSubProfileId", "AvitoItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkerAvitoAds_WorkerId_AccountId_IsActive",
                table: "WorkerAvitoAds",
                columns: new[] { "WorkerId", "AccountId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerAvitoAds");
        }
    }
}
