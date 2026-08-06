using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringCycleJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitoringCycleRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IngestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringCycleRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitoringCycleRuns_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitoringSubProfileRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubProfileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SubProfileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FoundCount = table.Column<int>(type: "integer", nullable: false),
                    PublishedCount = table.Column<int>(type: "integer", nullable: false),
                    DeferredCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedDuplicateCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringSubProfileRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitoringSubProfileRuns_MonitoringCycleRuns_CycleRunId",
                        column: x => x.CycleRunId,
                        principalTable: "MonitoringCycleRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringCycleRuns_AccountId_StartedAtUtc",
                table: "MonitoringCycleRuns",
                columns: new[] { "AccountId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringCycleRuns_WorkerId_StartedAtUtc",
                table: "MonitoringCycleRuns",
                columns: new[] { "WorkerId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringSubProfileRuns_CycleRunId_Position",
                table: "MonitoringSubProfileRuns",
                columns: new[] { "CycleRunId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringSubProfileRuns_StartedAtUtc",
                table: "MonitoringSubProfileRuns",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitoringSubProfileRuns");

            migrationBuilder.DropTable(
                name: "MonitoringCycleRuns");
        }
    }
}
