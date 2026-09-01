using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260829203000_AddTelephonyProviderAccounts")]
public partial class AddTelephonyProviderAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CrmCalls_OfficeId_Provider_ExternalCallId",
            table: "CrmCalls");

        migrationBuilder.AddColumn<Guid>(
            name: "ProviderAccountId",
            table: "CrmCalls",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RecordingArchiveAttempts",
            table: "CrmCalls",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "NextRecordingArchiveAtUtc",
            table: "CrmCalls",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "CrmTelephonyProviderAccounts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ExternalAccountId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                AccessTokenProtected = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                OwnedNumbersJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                SecretHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                SyncFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                SyncCursorUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastSyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                SyncStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                LastSyncError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CrmTelephonyProviderAccounts", x => x.Id);
                table.ForeignKey(
                    name: "FK_CrmTelephonyProviderAccounts_Offices_OfficeId",
                    column: x => x.OfficeId,
                    principalTable: "Offices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CrmTelephonyProviderAccountBindings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderUserKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CrmTelephonyProviderAccountBindings", x => x.Id);
                table.ForeignKey(
                    name: "FK_CrmTelephonyProviderAccountBindings_CrmTelephonyProviderAccounts_ProviderAccountId",
                    column: x => x.ProviderAccountId,
                    principalTable: "CrmTelephonyProviderAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CrmCalls_OfficeId_Provider_ExternalCallId",
            table: "CrmCalls",
            columns: new[] { "OfficeId", "Provider", "ExternalCallId" },
            unique: true,
            filter: "\"ProviderAccountId\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_CrmCalls_ProviderAccountId_ExternalCallId",
            table: "CrmCalls",
            columns: new[] { "ProviderAccountId", "ExternalCallId" },
            unique: true,
            filter: "\"ProviderAccountId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_CrmCalls_Provider_NextRecordingArchiveAtUtc",
            table: "CrmCalls",
            columns: new[] { "Provider", "NextRecordingArchiveAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CrmTelephonyProviderAccountBindings_ProviderAccountId_ProviderUserKey",
            table: "CrmTelephonyProviderAccountBindings",
            columns: new[] { "ProviderAccountId", "ProviderUserKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CrmTelephonyProviderAccountBindings_ProviderAccountId_UserId",
            table: "CrmTelephonyProviderAccountBindings",
            columns: new[] { "ProviderAccountId", "UserId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CrmTelephonyProviderAccounts_OfficeId_Provider_ExternalAccountId",
            table: "CrmTelephonyProviderAccounts",
            columns: new[] { "OfficeId", "Provider", "ExternalAccountId" },
            unique: true,
            filter: "\"ExternalAccountId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_CrmTelephonyProviderAccounts_OfficeId_Provider_Name",
            table: "CrmTelephonyProviderAccounts",
            columns: new[] { "OfficeId", "Provider", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CrmTelephonyProviderAccounts_Provider_IsEnabled_SyncCursorUtc",
            table: "CrmTelephonyProviderAccounts",
            columns: new[] { "Provider", "IsEnabled", "SyncCursorUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CrmTelephonyProviderAccounts_PublicId",
            table: "CrmTelephonyProviderAccounts",
            column: "PublicId",
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_CrmCalls_CrmTelephonyProviderAccounts_ProviderAccountId",
            table: "CrmCalls",
            column: "ProviderAccountId",
            principalTable: "CrmTelephonyProviderAccounts",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.Sql(
            """
            UPDATE "CrmCalls"
            SET "NextRecordingArchiveAtUtc" = NOW()
            WHERE "RecordingUrl" IS NOT NULL
              AND "RecordingUrl" <> ''
              AND "RecordingStoragePath" IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_CrmCalls_CrmTelephonyProviderAccounts_ProviderAccountId",
            table: "CrmCalls");

        migrationBuilder.DropTable(name: "CrmTelephonyProviderAccountBindings");
        migrationBuilder.DropTable(name: "CrmTelephonyProviderAccounts");

        migrationBuilder.DropIndex(name: "IX_CrmCalls_OfficeId_Provider_ExternalCallId", table: "CrmCalls");
        migrationBuilder.DropIndex(name: "IX_CrmCalls_ProviderAccountId_ExternalCallId", table: "CrmCalls");
        migrationBuilder.DropIndex(name: "IX_CrmCalls_Provider_NextRecordingArchiveAtUtc", table: "CrmCalls");

        migrationBuilder.DropColumn(name: "ProviderAccountId", table: "CrmCalls");
        migrationBuilder.DropColumn(name: "RecordingArchiveAttempts", table: "CrmCalls");
        migrationBuilder.DropColumn(name: "NextRecordingArchiveAtUtc", table: "CrmCalls");

        migrationBuilder.CreateIndex(
            name: "IX_CrmCalls_OfficeId_Provider_ExternalCallId",
            table: "CrmCalls",
            columns: new[] { "OfficeId", "Provider", "ExternalCallId" },
            unique: true);
    }
}
