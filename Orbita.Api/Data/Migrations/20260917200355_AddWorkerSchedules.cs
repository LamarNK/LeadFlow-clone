using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkerScheduleOffices",
                columns: table => new
                {
                    OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                    DayStartLocalTime = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    DayEndLocalTime = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    NightStartLocalTime = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    NightEndLocalTime = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerScheduleOffices", x => x.OfficeId);
                    table.ForeignKey(
                        name: "FK_WorkerScheduleOffices_Offices_OfficeId",
                        column: x => x.OfficeId,
                        principalTable: "Offices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkerScheduleGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                    DayOff = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CurrentWeekShift = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastAppliedOffDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerScheduleGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkerScheduleGroups_WorkerScheduleOffices_OfficeId",
                        column: x => x.OfficeId,
                        principalTable: "WorkerScheduleOffices",
                        principalColumn: "OfficeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkerScheduleAssignments",
                columns: table => new
                {
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Shift = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerScheduleAssignments", x => x.WorkerId);
                    table.ForeignKey(
                        name: "FK_WorkerScheduleAssignments_WorkerScheduleGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "WorkerScheduleGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkerScheduleAssignments_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerScheduleAssignments_GroupId_Shift",
                table: "WorkerScheduleAssignments",
                columns: new[] { "GroupId", "Shift" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerScheduleGroups_OfficeId_DayOff",
                table: "WorkerScheduleGroups",
                columns: new[] { "OfficeId", "DayOff" },
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO "WorkerScheduleOffices" (
                    "OfficeId", "DayStartLocalTime", "DayEndLocalTime", "NightStartLocalTime", "NightEndLocalTime",
                    "TimeZoneId", "UpdatedAtUtc", "UpdatedByUserId")
                SELECT "Id", '07:00', '19:00', '19:00', '07:00', 'Europe/Moscow', NOW(), NULL
                FROM "Offices"
                ON CONFLICT ("OfficeId") DO NOTHING;

                INSERT INTO "WorkerScheduleGroups" (
                    "Id", "OfficeId", "DayOff", "Name", "CurrentWeekShift", "LastAppliedOffDate")
                SELECT gen_random_uuid(), office."Id", day."DayOff", day."Name",
                    CASE
                        WHEN (EXTRACT(ISODOW FROM NOW() AT TIME ZONE 'Europe/Moscow')::integer - 1) >= day."DayOff"
                            THEN CASE WHEN day."DayOff" % 2 = 0 THEN 'DAY_FIRST' ELSE 'NIGHT_FIRST' END
                        ELSE CASE WHEN day."DayOff" % 2 = 0 THEN 'NIGHT_FIRST' ELSE 'DAY_FIRST' END
                    END,
                    (NOW() AT TIME ZONE 'Europe/Moscow')::date
                        - (((EXTRACT(ISODOW FROM NOW() AT TIME ZONE 'Europe/Moscow')::integer - 1) - day."DayOff" + 7) % 7)
                FROM "Offices" office
                CROSS JOIN (VALUES
                    (0, 'Понедельник'), (1, 'Вторник'), (2, 'Среда'), (3, 'Четверг'),
                    (4, 'Пятница'), (5, 'Суббота'), (6, 'Воскресенье')
                ) AS day("DayOff", "Name")
                ON CONFLICT ("OfficeId", "DayOff") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkerScheduleAssignments");

            migrationBuilder.DropTable(
                name: "WorkerScheduleGroups");

            migrationBuilder.DropTable(
                name: "WorkerScheduleOffices");
        }
    }
}
