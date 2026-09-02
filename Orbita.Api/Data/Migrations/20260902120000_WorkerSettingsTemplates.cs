using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260902120000_WorkerSettingsTemplates")]
public partial class WorkerSettingsTemplates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WorkerSettingsTemplates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                NameNormalized = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                MaxConcurrentAccounts = table.Column<int>(type: "integer", nullable: false),
                ResponseFilterEnabled = table.Column<bool>(type: "boolean", nullable: false),
                ResponseFilterExcludeFemale = table.Column<bool>(type: "boolean", nullable: false),
                ResponseFilterExcludeMale = table.Column<bool>(type: "boolean", nullable: false),
                ResponseFilterMaxAgeMale = table.Column<int>(type: "integer", nullable: true),
                ResponseFilterMaxAgeFemale = table.Column<int>(type: "integer", nullable: true),
                ResponseFilterMaxAgeDays = table.Column<int>(type: "integer", nullable: true),
                ResponseHighlightEnabled = table.Column<bool>(type: "boolean", nullable: false),
                ResponseHighlightAgeBuckets = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                AutoScheduleEnabled = table.Column<bool>(type: "boolean", nullable: false),
                AutoScheduleDays = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                AutoScheduleFromLocalTime = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                AutoScheduleToLocalTime = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                MessengerAutoReplyEnabled = table.Column<bool>(type: "boolean", nullable: false),
                MessengerAutoReplyMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                PhoneUnchangedHours = table.Column<int>(type: "integer", nullable: true),
                AutoDeliverToCrm = table.Column<bool>(type: "boolean", nullable: false),
                AutoDeliverToBitrix = table.Column<bool>(type: "boolean", nullable: false),
                AdsPowerEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                MultiloginEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                LocalChromeEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkerSettingsTemplates", x => x.Id);
                table.ForeignKey(
                    name: "FK_WorkerSettingsTemplates_Offices_OfficeId",
                    column: x => x.OfficeId,
                    principalTable: "Offices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WorkerSettingsTemplates_OfficeId_NameNormalized",
            table: "WorkerSettingsTemplates",
            columns: new[] { "OfficeId", "NameNormalized" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "WorkerSettingsTemplates");
    }
}
