using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260911062814_AddCandidatePhoneWatches")]
public sealed class AddCandidatePhoneWatches : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "WatchRefreshedCount",
            table: "MonitoringSubProfileRuns",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "PhoneChangedCount",
            table: "MonitoringSubProfileRuns",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "CandidatePhoneWatches",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                AvitoSubProfileId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                FullName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                FullNameKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                CanonicalResponseId = table.Column<Guid>(type: "uuid", nullable: true),
                PublishedSourceResponseId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CurrentPhoneRaw = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CurrentPhoneNormalized = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                LastPublishedPhoneNormalized = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                PhoneFirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                WatchStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                MessengerUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                ChatMessagesJson = table.Column<string>(type: "text", nullable: false),
                ChatFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ProfileFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CandidatePhoneWatches", x => x.Id);
                table.ForeignKey(
                    name: "FK_CandidatePhoneWatches_CandidatePersons_PersonId",
                    column: x => x.PersonId,
                    principalTable: "CandidatePersons",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CandidatePhoneWatches_CandidateResponses_CanonicalResponseId",
                    column: x => x.CanonicalResponseId,
                    principalTable: "CandidateResponses",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_CandidatePhoneWatches_Workers_WorkerId",
                    column: x => x.WorkerId,
                    principalTable: "Workers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CandidatePhoneWatches_AccountId_AvitoSubProfileId_FullNameKey",
            table: "CandidatePhoneWatches",
            columns: new[] { "AccountId", "AvitoSubProfileId", "FullNameKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CandidatePhoneWatches_CanonicalResponseId",
            table: "CandidatePhoneWatches",
            column: "CanonicalResponseId");

        migrationBuilder.CreateIndex(
            name: "IX_CandidatePhoneWatches_PersonId",
            table: "CandidatePhoneWatches",
            column: "PersonId");

        migrationBuilder.CreateIndex(
            name: "IX_CandidatePhoneWatches_WorkerId_State_ExpiresAtUtc",
            table: "CandidatePhoneWatches",
            columns: new[] { "WorkerId", "State", "ExpiresAtUtc" });

        migrationBuilder.Sql(
            """
            WITH ranked AS (
                SELECT
                    r.*,
                    lower(trim(regexp_replace(r."FullName", '\s+', ' ', 'g'))) AS full_name_key,
                    min(r."CollectedAt") OVER (
                        PARTITION BY r."AccountId", r."AvitoSubProfileId",
                            lower(trim(regexp_replace(r."FullName", '\s+', ' ', 'g')))
                    ) AS watch_started_utc,
                    row_number() OVER (
                        PARTITION BY r."AccountId", r."AvitoSubProfileId",
                            lower(trim(regexp_replace(r."FullName", '\s+', ' ', 'g')))
                        ORDER BY r."CollectedAt" DESC, r."Id" DESC
                    ) AS row_number
                FROM "CandidateResponses" r
                WHERE r."WorkerId" IS NOT NULL
                  AND r."SourceResponseId" LIKE 'phone-watch:%'
                  AND trim(r."FullName") <> ''
            ),
            latest AS (
                SELECT ranked.*
                FROM ranked
                WHERE row_number = 1
            )
            INSERT INTO "CandidatePhoneWatches" (
                "Id", "WorkerId", "AccountId", "AvitoSubProfileId",
                "FullName", "FullNameKey", "PersonId", "CanonicalResponseId",
                "PublishedSourceResponseId", "CurrentPhoneRaw", "CurrentPhoneNormalized",
                "LastPublishedPhoneNormalized", "PhoneFirstSeenUtc", "LastSeenUtc",
                "WatchStartedUtc", "ExpiresAtUtc", "State", "MessengerUrl",
                "ChatMessagesJson", "ChatFingerprint", "ProfileFingerprint",
                "CreatedAtUtc", "UpdatedAtUtc")
            SELECT
                latest."Id",
                latest."WorkerId",
                latest."AccountId",
                latest."AvitoSubProfileId",
                latest."FullName",
                latest.full_name_key,
                latest."PersonId",
                canonical."Id",
                latest."SourceResponseId",
                latest."PhoneRaw",
                latest."PhoneNormalized",
                latest."PhoneNormalized",
                latest.watch_started_utc,
                latest."CollectedAt",
                latest.watch_started_utc,
                latest.watch_started_utc
                    + greatest(0, coalesce(w."PhoneUnchangedHours", 120)) * interval '1 hour',
                CASE
                    WHEN latest.watch_started_utc
                        + greatest(0, coalesce(w."PhoneUnchangedHours", 120)) * interval '1 hour' <= now()
                        THEN 'Expired'
                    WHEN latest."PhoneMetricKind" = 'PhoneChanged' THEN 'Changed'
                    ELSE 'Open'
                END,
                latest."MessengerUrl",
                latest."ChatMessagesJson",
                '',
                '',
                latest.watch_started_utc,
                latest."CollectedAt"
            FROM latest
            JOIN "Workers" w ON w."Id" = latest."WorkerId"
            LEFT JOIN LATERAL (
                SELECT candidate."Id"
                FROM "CandidateResponses" candidate
                WHERE candidate."PersonId" = latest."PersonId"
                  AND candidate."Status" <> 'Duplicate'
                ORDER BY candidate."CollectedAt" DESC, candidate."Id" DESC
                LIMIT 1
            ) canonical ON true
            ON CONFLICT ("AccountId", "AvitoSubProfileId", "FullNameKey") DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CandidatePhoneWatches");
        migrationBuilder.DropColumn(name: "PhoneChangedCount", table: "MonitoringSubProfileRuns");
        migrationBuilder.DropColumn(name: "WatchRefreshedCount", table: "MonitoringSubProfileRuns");
    }
}
