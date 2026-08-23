using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260817180000_AddCrmTelephonyCalls")]
public partial class AddCrmTelephonyCalls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CrmTelephonyWebhooks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                SecretHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CrmTelephonyWebhooks", x => x.Id);
                table.ForeignKey(
                    name: "FK_CrmTelephonyWebhooks_Offices_OfficeId",
                    column: x => x.OfficeId,
                    principalTable: "Offices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CrmTelephonyUserBindings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ProviderUserKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CrmTelephonyUserBindings", x => x.Id);
                table.ForeignKey(
                    name: "FK_CrmTelephonyUserBindings_Offices_OfficeId",
                    column: x => x.OfficeId,
                    principalTable: "Offices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CrmCalls",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                CardId = table.Column<Guid>(type: "uuid", nullable: true),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ExternalCallId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Direction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CallerPhone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CalledPhone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ClientPhoneNormalized = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ProviderUserKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ManagerUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                RecordingUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CrmCalls", x => x.Id);
                table.ForeignKey(
                    name: "FK_CrmCalls_CrmCandidateCards_CardId",
                    column: x => x.CardId,
                    principalTable: "CrmCandidateCards",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_CrmCalls_Offices_OfficeId",
                    column: x => x.OfficeId,
                    principalTable: "Offices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CrmCalls_CardId_StartedAtUtc",
            table: "CrmCalls",
            columns: new[] { "CardId", "StartedAtUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_CrmCalls_OfficeId_ClientPhoneNormalized_StartedAtUtc",
            table: "CrmCalls",
            columns: new[] { "OfficeId", "ClientPhoneNormalized", "StartedAtUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_CrmCalls_OfficeId_Provider_ExternalCallId",
            table: "CrmCalls",
            columns: new[] { "OfficeId", "Provider", "ExternalCallId" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_CrmTelephonyUserBindings_OfficeId_Provider_ProviderUserKey",
            table: "CrmTelephonyUserBindings",
            columns: new[] { "OfficeId", "Provider", "ProviderUserKey" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_CrmTelephonyUserBindings_OfficeId_Provider_UserId",
            table: "CrmTelephonyUserBindings",
            columns: new[] { "OfficeId", "Provider", "UserId" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_CrmTelephonyWebhooks_OfficeId_Provider",
            table: "CrmTelephonyWebhooks",
            columns: new[] { "OfficeId", "Provider" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_CrmTelephonyWebhooks_PublicId",
            table: "CrmTelephonyWebhooks",
            column: "PublicId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CrmCalls");
        migrationBuilder.DropTable(name: "CrmTelephonyUserBindings");
        migrationBuilder.DropTable(name: "CrmTelephonyWebhooks");
    }
}
