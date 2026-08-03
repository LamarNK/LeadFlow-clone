using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBitrixWorkforceDistribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BitrixDealEventInbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BitrixInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DealId = table.Column<long>(type: "bigint", nullable: false),
                    EventKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    FailureCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LockedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BitrixDealEventInbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BitrixDealEventInbox_BitrixInstances_BitrixInstanceId",
                        column: x => x.BitrixInstanceId,
                        principalTable: "BitrixInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BitrixWorkforceConfigurations",
                columns: table => new
                {
                    BitrixInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DealCategoryId = table.Column<int>(type: "integer", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MorningWindowStartMinutes = table.Column<int>(type: "integer", nullable: false),
                    MorningWindowEndMinutes = table.Column<int>(type: "integer", nullable: false),
                    LateJoinReserveMinutes = table.Column<int>(type: "integer", nullable: false),
                    SingleManagerInitialReleasePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    RetryDelaySeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    PreserveManualNewOwner = table.Column<bool>(type: "boolean", nullable: false),
                    SyncContactOwner = table.Column<bool>(type: "boolean", nullable: false),
                    FillOnlyEmptyAvitoFields = table.Column<bool>(type: "boolean", nullable: false),
                    WriterRulesConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BitrixWorkforceConfigurations", x => x.BitrixInstanceId);
                    table.ForeignKey(
                        name: "FK_BitrixWorkforceConfigurations_BitrixInstances_BitrixInstanc~",
                        column: x => x.BitrixInstanceId,
                        principalTable: "BitrixInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BitrixWorkforceCursors",
                columns: table => new
                {
                    BitrixInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scenario = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastAssignedBitrixUserId = table.Column<long>(type: "bigint", nullable: true),
                    LastAssignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BitrixWorkforceCursors", x => new { x.BitrixInstanceId, x.Scenario });
                    table.ForeignKey(
                        name: "FK_BitrixWorkforceCursors_BitrixInstances_BitrixInstanceId",
                        column: x => x.BitrixInstanceId,
                        principalTable: "BitrixInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BitrixWorkforceDealStates",
                columns: table => new
                {
                    BitrixInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DealId = table.Column<long>(type: "bigint", nullable: false),
                    LastObservedStageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastAppliedStageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastAppliedResponsibleId = table.Column<long>(type: "bigint", nullable: true),
                    LastAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BitrixWorkforceDealStates", x => new { x.BitrixInstanceId, x.DealId });
                    table.ForeignKey(
                        name: "FK_BitrixWorkforceDealStates_BitrixInstances_BitrixInstanceId",
                        column: x => x.BitrixInstanceId,
                        principalTable: "BitrixInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BitrixWorkforceEventCredentials",
                columns: table => new
                {
                    BitrixInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExpectedMemberId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ConfiguredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BitrixWorkforceEventCredentials", x => x.BitrixInstanceId);
                    table.ForeignKey(
                        name: "FK_BitrixWorkforceEventCredentials_BitrixInstances_BitrixInsta~",
                        column: x => x.BitrixInstanceId,
                        principalTable: "BitrixInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BitrixWorkforceManagers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BitrixInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BitrixUserId = table.Column<long>(type: "bigint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BitrixWorkforceManagers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BitrixWorkforceManagers_BitrixInstances_BitrixInstanceId",
                        column: x => x.BitrixInstanceId,
                        principalTable: "BitrixInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BitrixWorkforceMorningStates",
                columns: table => new
                {
                    BitrixInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Scenario = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FirstManagerId = table.Column<long>(type: "bigint", nullable: true),
                    FirstManagerSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReserveUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InitialReleaseLimit = table.Column<int>(type: "integer", nullable: false),
                    InitialReleasedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BitrixWorkforceMorningStates", x => new { x.BitrixInstanceId, x.LocalDate, x.Scenario });
                    table.ForeignKey(
                        name: "FK_BitrixWorkforceMorningStates_BitrixInstances_BitrixInstance~",
                        column: x => x.BitrixInstanceId,
                        principalTable: "BitrixInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BitrixWorkforceStageRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BitrixInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scenario = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceStageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetStageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UsesMorningWindow = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BitrixWorkforceStageRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BitrixWorkforceStageRules_BitrixInstances_BitrixInstanceId",
                        column: x => x.BitrixInstanceId,
                        principalTable: "BitrixInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BitrixWorkforceAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InboxId = table.Column<long>(type: "bigint", nullable: false),
                    BitrixInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DealId = table.Column<long>(type: "bigint", nullable: false),
                    ContactId = table.Column<long>(type: "bigint", nullable: true),
                    Scenario = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FromStageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ToStageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PreviousResponsibleId = table.Column<long>(type: "bigint", nullable: true),
                    SelectedResponsibleId = table.Column<long>(type: "bigint", nullable: true),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfigurationRevisionAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DealAppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ContactsAppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BitrixWorkforceAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BitrixWorkforceAssignments_BitrixDealEventInbox_InboxId",
                        column: x => x.InboxId,
                        principalTable: "BitrixDealEventInbox",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BitrixWorkforceAssignments_BitrixInstances_BitrixInstanceId",
                        column: x => x.BitrixInstanceId,
                        principalTable: "BitrixInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BitrixDealEventInbox_BitrixInstanceId_EventKey",
                table: "BitrixDealEventInbox",
                columns: new[] { "BitrixInstanceId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BitrixDealEventInbox_State_NextAttemptAtUtc",
                table: "BitrixDealEventInbox",
                columns: new[] { "State", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BitrixWorkforceAssignments_BitrixInstanceId_DealId",
                table: "BitrixWorkforceAssignments",
                columns: new[] { "BitrixInstanceId", "DealId" },
                unique: true,
                filter: "\"AppliedAtUtc\" IS NULL AND \"Decision\" = 'assigned'");

            migrationBuilder.CreateIndex(
                name: "IX_BitrixWorkforceAssignments_BitrixInstanceId_DealId_CreatedA~",
                table: "BitrixWorkforceAssignments",
                columns: new[] { "BitrixInstanceId", "DealId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BitrixWorkforceAssignments_InboxId",
                table: "BitrixWorkforceAssignments",
                column: "InboxId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BitrixWorkforceEventCredentials_PublicId",
                table: "BitrixWorkforceEventCredentials",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BitrixWorkforceManagers_BitrixInstanceId_BitrixUserId",
                table: "BitrixWorkforceManagers",
                columns: new[] { "BitrixInstanceId", "BitrixUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BitrixWorkforceManagers_BitrixInstanceId_SortOrder",
                table: "BitrixWorkforceManagers",
                columns: new[] { "BitrixInstanceId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_BitrixWorkforceStageRules_BitrixInstanceId_SourceStageId",
                table: "BitrixWorkforceStageRules",
                columns: new[] { "BitrixInstanceId", "SourceStageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BitrixWorkforceAssignments");

            migrationBuilder.DropTable(
                name: "BitrixWorkforceConfigurations");

            migrationBuilder.DropTable(
                name: "BitrixWorkforceCursors");

            migrationBuilder.DropTable(
                name: "BitrixWorkforceDealStates");

            migrationBuilder.DropTable(
                name: "BitrixWorkforceEventCredentials");

            migrationBuilder.DropTable(
                name: "BitrixWorkforceManagers");

            migrationBuilder.DropTable(
                name: "BitrixWorkforceMorningStates");

            migrationBuilder.DropTable(
                name: "BitrixWorkforceStageRules");

            migrationBuilder.DropTable(
                name: "BitrixDealEventInbox");
        }
    }
}
