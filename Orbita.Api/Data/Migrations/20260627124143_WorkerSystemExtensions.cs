using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkerSystemExtensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdsPowerApiBaseUrl",
                table: "Workers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdsPowerApiKey",
                table: "Workers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastCpuPercent",
                table: "Workers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastRamPercent",
                table: "Workers",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastRamTotalMb",
                table: "Workers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastRamUsedMb",
                table: "Workers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentAccounts",
                table: "Workers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AdsPowerProfileId",
                table: "WorkerAccounts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsEnabledInPanel",
                table: "WorkerAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CandidateResponses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountName = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    SourceResponseId = table.Column<string>(type: "text", nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: false),
                    FirstName = table.Column<string>(type: "text", nullable: false),
                    LastName = table.Column<string>(type: "text", nullable: false),
                    MiddleName = table.Column<string>(type: "text", nullable: false),
                    Age = table.Column<int>(type: "integer", nullable: true),
                    PhoneRaw = table.Column<string>(type: "text", nullable: false),
                    PhoneNormalized = table.Column<string>(type: "text", nullable: false),
                    City = table.Column<string>(type: "text", nullable: false),
                    Vacancy = table.Column<string>(type: "text", nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: false),
                    VacancyUrl = table.Column<string>(type: "text", nullable: false),
                    MessengerUrl = table.Column<string>(type: "text", nullable: false),
                    AvitoSubProfileId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    BitrixEntityType = table.Column<string>(type: "text", nullable: false),
                    BitrixEntityId = table.Column<string>(type: "text", nullable: false),
                    BitrixContactId = table.Column<string>(type: "text", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: false),
                    RawText = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CandidateResponses_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateResponses_AccountId_SourceResponseId_PhoneNormaliz~",
                table: "CandidateResponses",
                columns: new[] { "AccountId", "SourceResponseId", "PhoneNormalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CandidateResponses_CreatedAt",
                table: "CandidateResponses",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateResponses_PhoneNormalized",
                table: "CandidateResponses",
                column: "PhoneNormalized");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateResponses_WorkerId",
                table: "CandidateResponses",
                column: "WorkerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CandidateResponses");

            migrationBuilder.DropColumn(
                name: "AdsPowerApiBaseUrl",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "AdsPowerApiKey",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LastCpuPercent",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LastRamPercent",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LastRamTotalMb",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LastRamUsedMb",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentAccounts",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "AdsPowerProfileId",
                table: "WorkerAccounts");

            migrationBuilder.DropColumn(
                name: "IsEnabledInPanel",
                table: "WorkerAccounts");
        }
    }
}
