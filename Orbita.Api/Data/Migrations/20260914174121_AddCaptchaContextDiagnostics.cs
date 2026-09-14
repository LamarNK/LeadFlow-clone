using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCaptchaContextDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ChallengePresent",
                table: "CaptchaProviderRequests",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContextAgeAtSubmitMs",
                table: "CaptchaProviderRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContextAgeAtVerifyMs",
                table: "CaptchaProviderRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContextFingerprint",
                table: "CaptchaProviderRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContextSource",
                table: "CaptchaProviderRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RiskTypePresent",
                table: "CaptchaProviderRequests",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SolveDurationMs",
                table: "CaptchaProviderRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetHttpStatus",
                table: "CaptchaProviderRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetReason",
                table: "CaptchaProviderRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChallengePresent",
                table: "CaptchaProviderRequests");

            migrationBuilder.DropColumn(
                name: "ContextAgeAtSubmitMs",
                table: "CaptchaProviderRequests");

            migrationBuilder.DropColumn(
                name: "ContextAgeAtVerifyMs",
                table: "CaptchaProviderRequests");

            migrationBuilder.DropColumn(
                name: "ContextFingerprint",
                table: "CaptchaProviderRequests");

            migrationBuilder.DropColumn(
                name: "ContextSource",
                table: "CaptchaProviderRequests");

            migrationBuilder.DropColumn(
                name: "RiskTypePresent",
                table: "CaptchaProviderRequests");

            migrationBuilder.DropColumn(
                name: "SolveDurationMs",
                table: "CaptchaProviderRequests");

            migrationBuilder.DropColumn(
                name: "TargetHttpStatus",
                table: "CaptchaProviderRequests");

            migrationBuilder.DropColumn(
                name: "TargetReason",
                table: "CaptchaProviderRequests");
        }
    }
}
