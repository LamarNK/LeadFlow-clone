using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260814150000_AddCrmDailyDistribution")]
public partial class AddCrmDailyDistribution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CrmDailyDistributionCounters",
            columns: table => new
            {
                OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                Pool = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ManagerUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                AssignedCount = table.Column<int>(type: "integer", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_CrmDailyDistributionCounters",
                    x => new { x.OfficeId, x.LocalDate, x.Pool, x.ManagerUserId });
                table.ForeignKey(
                    name: "FK_CrmDailyDistributionCounters_Offices_OfficeId",
                    column: x => x.OfficeId,
                    principalTable: "Offices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CrmDailyDistributionSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                FirstShiftStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DistributeAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DistributedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ManagerRosterJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                LastLeadManagerUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CrmDailyDistributionSessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_CrmDailyDistributionSessions_Offices_OfficeId",
                    column: x => x.OfficeId,
                    principalTable: "Offices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CrmDailyDistributionSessions_DistributedAtUtc_DistributeAfterUtc",
            table: "CrmDailyDistributionSessions",
            columns: new[] { "DistributedAtUtc", "DistributeAfterUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CrmDailyDistributionSessions_OfficeId_LocalDate",
            table: "CrmDailyDistributionSessions",
            columns: new[] { "OfficeId", "LocalDate" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CrmDailyDistributionCounters");
        migrationBuilder.DropTable(name: "CrmDailyDistributionSessions");
    }
}
