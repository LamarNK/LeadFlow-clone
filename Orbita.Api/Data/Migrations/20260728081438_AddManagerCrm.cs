using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManagerCrm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CrmCapacity",
                table: "PanelUserProfiles",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<DateTime>(
                name: "CrmLastAutoAssignmentAtUtc",
                table: "PanelUserProfiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CrmShiftActive",
                table: "PanelUserProfiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CrmEnabled",
                table: "Offices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CrmCandidateCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponseId = table.Column<Guid>(type: "uuid", nullable: false),
                    OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ManagerUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IsInActiveLoad = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmCandidateCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrmCandidateCards_CandidateResponses_ResponseId",
                        column: x => x.ResponseId,
                        principalTable: "CandidateResponses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CrmCandidateHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ActorUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ActorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmCandidateHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CrmCandidateNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AuthorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmCandidateNotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CrmTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AssigneeUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatorUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrmCandidateCards_OfficeId_ManagerUserId_IsInActiveLoad",
                table: "CrmCandidateCards",
                columns: new[] { "OfficeId", "ManagerUserId", "IsInActiveLoad" });

            migrationBuilder.CreateIndex(
                name: "IX_CrmCandidateCards_OfficeId_Stage",
                table: "CrmCandidateCards",
                columns: new[] { "OfficeId", "Stage" });

            migrationBuilder.CreateIndex(
                name: "IX_CrmCandidateCards_ResponseId",
                table: "CrmCandidateCards",
                column: "ResponseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CrmCandidateHistory_CardId_CreatedAtUtc",
                table: "CrmCandidateHistory",
                columns: new[] { "CardId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CrmCandidateNotes_CardId_CreatedAtUtc",
                table: "CrmCandidateNotes",
                columns: new[] { "CardId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CrmTasks_CardId",
                table: "CrmTasks",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_CrmTasks_OfficeId_AssigneeUserId_Status",
                table: "CrmTasks",
                columns: new[] { "OfficeId", "AssigneeUserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrmCandidateCards");

            migrationBuilder.DropTable(
                name: "CrmCandidateHistory");

            migrationBuilder.DropTable(
                name: "CrmCandidateNotes");

            migrationBuilder.DropTable(
                name: "CrmTasks");

            migrationBuilder.DropColumn(
                name: "CrmCapacity",
                table: "PanelUserProfiles");

            migrationBuilder.DropColumn(
                name: "CrmLastAutoAssignmentAtUtc",
                table: "PanelUserProfiles");

            migrationBuilder.DropColumn(
                name: "CrmShiftActive",
                table: "PanelUserProfiles");

            migrationBuilder.DropColumn(
                name: "CrmEnabled",
                table: "Offices");

        }
    }
}
