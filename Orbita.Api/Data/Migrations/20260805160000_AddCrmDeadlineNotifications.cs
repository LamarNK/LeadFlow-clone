using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCrmDeadlineNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CrmDeadlineNotificationsEnabled",
                table: "Offices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CrmDeadlineNotificationsEnabledAtUtc",
                table: "Offices",
                type: "timestamp with time zone",
                nullable: true);

            // Add reminder metadata as nullable first so existing rows can receive
            // a distinct version and a meaningful timestamp before NOT NULL is applied.
            migrationBuilder.AddColumn<Guid>(
                name: "ReminderVersion",
                table: "CrmTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderVersionChangedAtUtc",
                table: "CrmTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "CrmTasks"
                SET "ReminderVersion" = gen_random_uuid(),
                    "ReminderVersionChangedAtUtc" = "CreatedAtUtc"
                WHERE "ReminderVersion" IS NULL
                   OR "ReminderVersionChangedAtUtc" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "ReminderVersion",
                table: "CrmTasks",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "ReminderVersionChangedAtUtc",
                table: "CrmTasks",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "CrmTaskNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReminderVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DismissedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmTaskNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrmTaskNotifications_CrmTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "CrmTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CrmTaskNotifications_Offices_OfficeId",
                        column: x => x.OfficeId,
                        principalTable: "Offices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrmTasks_OfficeId_Status_DueAtUtc",
                table: "CrmTasks",
                columns: new[] { "OfficeId", "Status", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CrmTaskNotifications_OfficeId_RecipientUserId_ReadAtUtc_Cre~",
                table: "CrmTaskNotifications",
                columns: new[] { "OfficeId", "RecipientUserId", "ReadAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CrmTaskNotifications_TaskId_ReminderVersion_Kind",
                table: "CrmTaskNotifications",
                columns: new[] { "TaskId", "ReminderVersion", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrmTaskNotifications");

            migrationBuilder.DropIndex(
                name: "IX_CrmTasks_OfficeId_Status_DueAtUtc",
                table: "CrmTasks");

            migrationBuilder.DropColumn(
                name: "CrmDeadlineNotificationsEnabled",
                table: "Offices");

            migrationBuilder.DropColumn(
                name: "CrmDeadlineNotificationsEnabledAtUtc",
                table: "Offices");

            migrationBuilder.DropColumn(
                name: "ReminderVersion",
                table: "CrmTasks");

            migrationBuilder.DropColumn(
                name: "ReminderVersionChangedAtUtc",
                table: "CrmTasks");
        }
    }
}
