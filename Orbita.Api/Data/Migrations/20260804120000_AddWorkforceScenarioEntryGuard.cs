using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    [DbContext(typeof(OrbitaDbContext))]
    [Migration("20260804120000_AddWorkforceScenarioEntryGuard")]
    public partial class AddWorkforceScenarioEntryGuard : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveScenario",
                table: "BitrixWorkforceDealStates",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActiveScenarioShadowHandledAtUtc",
                table: "BitrixWorkforceDealStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActiveScenarioWriterHandledAtUtc",
                table: "BitrixWorkforceDealStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperationMode",
                table: "BitrixWorkforceCursors",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "writer");

            migrationBuilder.AddColumn<string>(
                name: "OperationMode",
                table: "BitrixWorkforceMorningStates",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "writer");

            migrationBuilder.Sql(
                """
                WITH latest_modes AS (
                    SELECT DISTINCT ON (
                               assignment."BitrixInstanceId",
                               assignment."Scenario")
                           assignment."BitrixInstanceId",
                           assignment."Scenario",
                           assignment."OperationMode"
                    FROM "BitrixWorkforceAssignments" AS assignment
                    WHERE assignment."OperationMode" IN ('shadow', 'writer')
                      AND assignment."SelectedResponsibleId" IS NOT NULL
                    ORDER BY assignment."BitrixInstanceId",
                             assignment."Scenario",
                             assignment."CreatedAtUtc" DESC,
                             assignment."Id" DESC
                )
                UPDATE "BitrixWorkforceCursors" AS cursor
                SET "OperationMode" = latest."OperationMode"
                FROM latest_modes AS latest
                WHERE latest."BitrixInstanceId" = cursor."BitrixInstanceId"
                  AND latest."Scenario" = cursor."Scenario";

                -- Morning reserve/quota rows are ephemeral and date-specific. Their legacy
                -- shared-mode history cannot be split reliably, so rebuild them on demand.
                DELETE FROM "BitrixWorkforceMorningStates";
                """);

            migrationBuilder.DropPrimaryKey(
                name: "PK_BitrixWorkforceCursors",
                table: "BitrixWorkforceCursors");

            migrationBuilder.DropPrimaryKey(
                name: "PK_BitrixWorkforceMorningStates",
                table: "BitrixWorkforceMorningStates");

            migrationBuilder.AddPrimaryKey(
                name: "PK_BitrixWorkforceCursors",
                table: "BitrixWorkforceCursors",
                columns: new[] { "BitrixInstanceId", "Scenario", "OperationMode" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_BitrixWorkforceMorningStates",
                table: "BitrixWorkforceMorningStates",
                columns: new[]
                {
                    "BitrixInstanceId",
                    "LocalDate",
                    "Scenario",
                    "OperationMode"
                });

            migrationBuilder.Sql(
                """
                WITH latest_protected_assignment AS (
                    SELECT DISTINCT ON (
                               assignment."BitrixInstanceId",
                               assignment."DealId")
                           assignment."Id",
                           assignment."BitrixInstanceId",
                           assignment."DealId",
                           assignment."Scenario",
                           assignment."OperationMode",
                           assignment."ToStageId",
                           assignment."SelectedResponsibleId",
                           COALESCE(
                               assignment."DealAppliedAtUtc",
                               assignment."AppliedAtUtc",
                               assignment."CreatedAtUtc") AS "HandledAtUtc",
                           (
                               assignment."DealAppliedAtUtc" IS NOT NULL
                               OR (
                                   assignment."OperationMode" = 'shadow'
                                   AND assignment."AppliedAtUtc" IS NOT NULL
                               )
                           ) AS "WasApplied"
                    FROM "BitrixWorkforceAssignments" AS assignment
                    INNER JOIN "BitrixWorkforceDealStates" AS current_state
                        ON current_state."BitrixInstanceId" = assignment."BitrixInstanceId"
                       AND current_state."DealId" = assignment."DealId"
                    INNER JOIN "BitrixDealEventInbox" AS inbox
                        ON inbox."Id" = assignment."InboxId"
                    WHERE (
                          assignment."SelectedResponsibleId" IS NOT NULL
                          OR assignment."Decision" IN ('ignored', 'failed')
                          OR (
                              assignment."Decision" IN ('deferred', 'reserved')
                              AND inbox."AttemptCount" > 1
                          )
                      )
                      AND assignment."OperationMode" IN ('shadow', 'writer')
                      AND (
                          UPPER(assignment."FromStageId") = UPPER(current_state."LastObservedStageId")
                          OR UPPER(assignment."ToStageId") = UPPER(current_state."LastObservedStageId")
                          OR EXISTS (
                              SELECT 1
                              FROM "BitrixWorkforceStageRules" AS scenario_rule
                              WHERE scenario_rule."BitrixInstanceId" = assignment."BitrixInstanceId"
                                AND scenario_rule."Scenario" = assignment."Scenario"
                                AND (
                                    UPPER(scenario_rule."SourceStageId") = UPPER(current_state."LastObservedStageId")
                                    OR UPPER(scenario_rule."TargetStageId") = UPPER(current_state."LastObservedStageId")
                                )
                          )
                      )
                    ORDER BY assignment."BitrixInstanceId",
                             assignment."DealId",
                             COALESCE(
                                 assignment."DealAppliedAtUtc",
                                 assignment."AppliedAtUtc",
                                 assignment."CreatedAtUtc") DESC,
                             assignment."CreatedAtUtc" DESC,
                             assignment."Id" DESC
                )
                UPDATE "BitrixWorkforceDealStates" AS state
                SET "ActiveScenario" = assignment."Scenario",
                    "ActiveScenarioShadowHandledAtUtc" = CASE
                        WHEN assignment."OperationMode" = 'shadow'
                        THEN assignment."HandledAtUtc"
                        ELSE NULL
                    END,
                    "ActiveScenarioWriterHandledAtUtc" = CASE
                        WHEN assignment."OperationMode" = 'writer'
                        THEN assignment."HandledAtUtc"
                        ELSE NULL
                    END,
                    "LastAppliedStageId" = CASE
                        WHEN assignment."WasApplied"
                        THEN assignment."ToStageId"
                        ELSE state."LastAppliedStageId"
                    END,
                    "LastAppliedResponsibleId" = CASE
                        WHEN assignment."WasApplied"
                        THEN assignment."SelectedResponsibleId"
                        ELSE state."LastAppliedResponsibleId"
                    END,
                    "LastAssignmentId" = assignment."Id",
                    "UpdatedAtUtc" = GREATEST(
                        state."UpdatedAtUtc",
                        assignment."HandledAtUtc")
                FROM latest_protected_assignment AS assignment
                WHERE state."BitrixInstanceId" = assignment."BitrixInstanceId"
                  AND state."DealId" = assignment."DealId";

                WITH latest_failed_observation AS (
                    SELECT DISTINCT ON (
                               state."BitrixInstanceId",
                               state."DealId")
                           state."BitrixInstanceId",
                           state."DealId",
                           rule."Scenario",
                           GREATEST(
                               state."UpdatedAtUtc",
                               inbox."ReceivedAtUtc") AS "HandledAtUtc"
                    FROM "BitrixWorkforceDealStates" AS state
                    INNER JOIN "BitrixDealEventInbox" AS inbox
                        ON inbox."BitrixInstanceId" = state."BitrixInstanceId"
                       AND inbox."DealId" = state."DealId"
                       AND inbox."FailureCount" > 0
                    INNER JOIN "BitrixWorkforceStageRules" AS rule
                        ON rule."BitrixInstanceId" = state."BitrixInstanceId"
                       AND (
                           UPPER(rule."SourceStageId") = UPPER(state."LastObservedStageId")
                           OR UPPER(rule."TargetStageId") = UPPER(state."LastObservedStageId")
                       )
                    LEFT JOIN "BitrixWorkforceAssignments" AS assignment
                        ON assignment."InboxId" = inbox."Id"
                    WHERE assignment."Id" IS NULL
                      AND state."ActiveScenario" IS NULL
                    ORDER BY state."BitrixInstanceId",
                             state."DealId",
                             inbox."ReceivedAtUtc" DESC,
                             inbox."Id" DESC,
                             rule."SortOrder"
                )
                UPDATE "BitrixWorkforceDealStates" AS state
                SET "ActiveScenario" = observation."Scenario",
                    "ActiveScenarioShadowHandledAtUtc" = observation."HandledAtUtc",
                    "ActiveScenarioWriterHandledAtUtc" = observation."HandledAtUtc",
                    "UpdatedAtUtc" = GREATEST(
                        state."UpdatedAtUtc",
                        observation."HandledAtUtc")
                FROM latest_failed_observation AS observation
                WHERE state."BitrixInstanceId" = observation."BitrixInstanceId"
                  AND state."DealId" = observation."DealId";

                -- Never let an installation boot directly into a legacy writer after
                -- this safety migration. Shadow must observe the current source-stage
                -- population under the new guard model before an administrator can
                -- explicitly pass the writer coverage gate again.
                UPDATE "BitrixWorkforceConfigurations"
                SET "OperationMode" = 'shadow',
                    "WriterRulesConfirmed" = FALSE,
                    "UpdatedAtUtc" = CURRENT_TIMESTAMP
                WHERE "OperationMode" IN ('shadow', 'writer');
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM "BitrixWorkforceCursors" AS shadow
                USING "BitrixWorkforceCursors" AS writer
                WHERE shadow."BitrixInstanceId" = writer."BitrixInstanceId"
                  AND shadow."Scenario" = writer."Scenario"
                  AND shadow."OperationMode" = 'shadow'
                  AND writer."OperationMode" = 'writer';

                DELETE FROM "BitrixWorkforceMorningStates" AS shadow
                USING "BitrixWorkforceMorningStates" AS writer
                WHERE shadow."BitrixInstanceId" = writer."BitrixInstanceId"
                  AND shadow."LocalDate" = writer."LocalDate"
                  AND shadow."Scenario" = writer."Scenario"
                  AND shadow."OperationMode" = 'shadow'
                  AND writer."OperationMode" = 'writer';
                """);

            migrationBuilder.DropPrimaryKey(
                name: "PK_BitrixWorkforceCursors",
                table: "BitrixWorkforceCursors");

            migrationBuilder.DropPrimaryKey(
                name: "PK_BitrixWorkforceMorningStates",
                table: "BitrixWorkforceMorningStates");

            migrationBuilder.DropColumn(
                name: "OperationMode",
                table: "BitrixWorkforceCursors");

            migrationBuilder.DropColumn(
                name: "OperationMode",
                table: "BitrixWorkforceMorningStates");

            migrationBuilder.AddPrimaryKey(
                name: "PK_BitrixWorkforceCursors",
                table: "BitrixWorkforceCursors",
                columns: new[] { "BitrixInstanceId", "Scenario" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_BitrixWorkforceMorningStates",
                table: "BitrixWorkforceMorningStates",
                columns: new[] { "BitrixInstanceId", "LocalDate", "Scenario" });

            migrationBuilder.DropColumn(
                name: "ActiveScenario",
                table: "BitrixWorkforceDealStates");

            migrationBuilder.DropColumn(
                name: "ActiveScenarioShadowHandledAtUtc",
                table: "BitrixWorkforceDealStates");

            migrationBuilder.DropColumn(
                name: "ActiveScenarioWriterHandledAtUtc",
                table: "BitrixWorkforceDealStates");
        }
    }
}
