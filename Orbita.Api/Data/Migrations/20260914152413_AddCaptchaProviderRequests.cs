using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCaptchaProviderRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaptchaProviderRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubProfileRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubProfileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SubProfileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CaptchaType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    ProviderStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderTaskId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    DiagnosticAttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptchaProviderRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaptchaProviderRequests_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaptchaProviderRequests_ProviderTaskId",
                table: "CaptchaProviderRequests",
                column: "ProviderTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_CaptchaProviderRequests_Stage_ProviderStatus_TargetStatus",
                table: "CaptchaProviderRequests",
                columns: new[] { "Stage", "ProviderStatus", "TargetStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_CaptchaProviderRequests_SubmittedAtUtc",
                table: "CaptchaProviderRequests",
                column: "SubmittedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CaptchaProviderRequests_WorkerId_AccountId_SubmittedAtUtc",
                table: "CaptchaProviderRequests",
                columns: new[] { "WorkerId", "AccountId", "SubmittedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaptchaProviderRequests");
        }
    }
}
