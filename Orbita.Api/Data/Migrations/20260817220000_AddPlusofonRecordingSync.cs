using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260817220000_AddPlusofonRecordingSync")]
public partial class AddPlusofonRecordingSync : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProviderAccessTokenProtected",
            table: "CrmTelephonyWebhooks",
            type: "character varying(8192)",
            maxLength: 8192,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ProviderClientId",
            table: "CrmTelephonyWebhooks",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "NextRecordingFetchAtUtc",
            table: "CrmCalls",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "RecordingFetchAttempts",
            table: "CrmCalls",
            type: "integer",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.CreateIndex(
            name: "IX_CrmCalls_Provider_NextRecordingFetchAtUtc",
            table: "CrmCalls",
            columns: new[] { "Provider", "NextRecordingFetchAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CrmCalls_Provider_NextRecordingFetchAtUtc",
            table: "CrmCalls");
        migrationBuilder.DropColumn(name: "ProviderAccessTokenProtected", table: "CrmTelephonyWebhooks");
        migrationBuilder.DropColumn(name: "ProviderClientId", table: "CrmTelephonyWebhooks");
        migrationBuilder.DropColumn(name: "NextRecordingFetchAtUtc", table: "CrmCalls");
        migrationBuilder.DropColumn(name: "RecordingFetchAttempts", table: "CrmCalls");
    }
}
