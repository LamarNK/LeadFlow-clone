using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CaptchaSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveCaptchaOperatorDisplayName",
                table: "Workers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActiveCaptchaOperatorUserId",
                table: "Workers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActiveCaptchaSessionId",
                table: "Workers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActiveCaptchaSessionStartedAtUtc",
                table: "Workers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CaptchaSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperatorUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OperatorDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CaptchaKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubProfileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ViewportWidth = table.Column<int>(type: "integer", nullable: false),
                    ViewportHeight = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptchaSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaptchaSessions_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaptchaSessions_WorkerId_Status",
                table: "CaptchaSessions",
                columns: new[] { "WorkerId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaptchaSessions");

            migrationBuilder.DropColumn(
                name: "ActiveCaptchaOperatorDisplayName",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "ActiveCaptchaOperatorUserId",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "ActiveCaptchaSessionId",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "ActiveCaptchaSessionStartedAtUtc",
                table: "Workers");
        }
    }
}
