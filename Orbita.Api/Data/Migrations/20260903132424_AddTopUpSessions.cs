using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTopUpSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TopUpPauseLeaseId",
                table: "Workers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TopUpPauseLeaseVersion",
                table: "Workers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "TopUpPauseBaselinePaused",
                table: "Workers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TopUpSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperatorUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OperatorDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurrentBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TargetBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RequestedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DailyResponseCount = table.Column<int>(type: "integer", nullable: false),
                    ExpectedPauseLeaseVersion = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    OwnsPauseLease = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaymentClaimedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QrReadyAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QrImageBase64 = table.Column<string>(type: "text", nullable: true),
                    QrImageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TopUpSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TopUpSessions_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TopUpSessions_Account_Active",
                table: "TopUpSessions",
                column: "AccountId",
                unique: true,
                filter: "\"Status\" IN ('requested', 'started', 'payment_claimed', 'qr_ready')");

            migrationBuilder.CreateIndex(
                name: "IX_TopUpSessions_WorkerId_AccountId_Status",
                table: "TopUpSessions",
                columns: new[] { "WorkerId", "AccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TopUpSessions_WorkerId_Status",
                table: "TopUpSessions",
                columns: new[] { "WorkerId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TopUpSessions");

            migrationBuilder.DropColumn(
                name: "TopUpPauseLeaseId",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "TopUpPauseLeaseVersion",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "TopUpPauseBaselinePaused",
                table: "Workers");
        }
    }
}
