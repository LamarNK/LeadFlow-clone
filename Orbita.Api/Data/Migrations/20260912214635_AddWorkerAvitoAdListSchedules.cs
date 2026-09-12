using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerAvitoAdListSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkerAvitoAdListSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AvitoSubProfileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastSuccessfulCheckAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextCheckAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerAvitoAdListSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkerAvitoAdListSchedules_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerAvitoAdListSchedules_NextCheckAtUtc",
                table: "WorkerAvitoAdListSchedules",
                column: "NextCheckAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkerAvitoAdListSchedules_WorkerId_AccountId_AvitoSubProfi~",
                table: "WorkerAvitoAdListSchedules",
                columns: new[] { "WorkerId", "AccountId", "AvitoSubProfileId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerAvitoAdListSchedules");
        }
    }
}
