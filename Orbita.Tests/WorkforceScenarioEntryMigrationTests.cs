using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

namespace Orbita.Tests;

public sealed class WorkforceScenarioEntryMigrationTests
{
    private const string PreviousMigrationId =
        "20260803231231_AddBitrixWorkforceDistribution";
    private const string MigrationId = "20260804120000_AddWorkforceScenarioEntryGuard";

    [Fact]
    public void Migration_IsDiscoverable()
    {
        using var db = CreateDb();

        Assert.Contains(MigrationId, db.Database.GetMigrations());
    }

    [Fact]
    public void Migration_AddsOperationModeToCursorAndMorningPrimaryKeys()
    {
        var script = NormalizeSql(GenerateUpScript());

        Assert.Contains(
            "ALTER TABLE \"BitrixWorkforceCursors\" ADD \"OperationMode\" character varying(16) NOT NULL DEFAULT 'writer'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER TABLE \"BitrixWorkforceMorningStates\" ADD \"OperationMode\" character varying(16) NOT NULL DEFAULT 'writer'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "PRIMARY KEY (\"BitrixInstanceId\", \"Scenario\", \"OperationMode\")",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "PRIMARY KEY (\"BitrixInstanceId\", \"LocalDate\", \"Scenario\", \"OperationMode\")",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_InfersLegacyCursorModeAndRebuildsEphemeralMorningState()
    {
        var script = NormalizeSql(GenerateUpScript());

        Assert.Equal(1, CountOccurrences(script, "WITH latest_modes AS"));
        Assert.Contains(
            "WHERE assignment.\"OperationMode\" IN ('shadow', 'writer') " +
            "AND assignment.\"SelectedResponsibleId\" IS NOT NULL " +
            "ORDER BY assignment.\"BitrixInstanceId\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "UPDATE \"BitrixWorkforceCursors\" AS cursor",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "UPDATE \"BitrixWorkforceMorningStates\" AS morning",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "DELETE FROM \"BitrixWorkforceMorningStates\"",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_BackfillsLatestProtectedAssignmentWithoutLastAssignmentDependency()
    {
        var script = NormalizeSql(GenerateUpScript());

        Assert.Contains("WITH latest_protected_assignment AS", script, StringComparison.Ordinal);
        Assert.Contains(
            "SELECT DISTINCT ON ( assignment.\"BitrixInstanceId\", assignment.\"DealId\")",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "FROM latest_protected_assignment AS assignment",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.\"BitrixInstanceId\" = assignment.\"BitrixInstanceId\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.\"DealId\" = assignment.\"DealId\"",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "state.\"LastAssignmentId\" = assignment.\"Id\"",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_BackfillProtectsAnySelectedAssignmentWithoutDecisionFilter()
    {
        var script = NormalizeSql(GenerateUpScript());

        Assert.Contains(
            "assignment.\"SelectedResponsibleId\" IS NOT NULL",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "assignment.\"Decision\" IN ('ignored', 'failed')",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "assignment.\"Decision\" IN ('assigned', 'failed')",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_BackfillProtectsRepeatedLegacyDeferredAndReservedAssignments()
    {
        var script = NormalizeSql(GenerateUpScript());

        Assert.Contains(
            "INNER JOIN \"BitrixDealEventInbox\" AS inbox " +
            "ON inbox.\"Id\" = assignment.\"InboxId\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "OR ( assignment.\"Decision\" IN ('deferred', 'reserved') " +
            "AND inbox.\"AttemptCount\" > 1 ) ) " +
            "AND assignment.\"OperationMode\" IN ('shadow', 'writer')",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_ProtectsLegacyFailuresBeforeDecisionCreation()
    {
        var script = NormalizeSql(GenerateUpScript());

        Assert.Contains("WITH latest_failed_observation AS", script, StringComparison.Ordinal);
        Assert.Contains("inbox.\"FailureCount\" > 0", script, StringComparison.Ordinal);
        Assert.Contains("assignment.\"Id\" IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("state.\"ActiveScenario\" IS NULL", script, StringComparison.Ordinal);
        Assert.Contains(
            "\"ActiveScenarioShadowHandledAtUtc\" = observation.\"HandledAtUtc\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"ActiveScenarioWriterHandledAtUtc\" = observation.\"HandledAtUtc\"",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_BackfillIsModeAwareAndRestoresAppliedState()
    {
        var script = NormalizeSql(GenerateUpScript());

        Assert.Contains(
            "assignment.\"OperationMode\" IN ('shadow', 'writer')",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHEN assignment.\"OperationMode\" = 'shadow'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHEN assignment.\"OperationMode\" = 'writer'",
            script,
            StringComparison.Ordinal);
        Assert.Contains("\"ActiveScenarioShadowHandledAtUtc\"", script, StringComparison.Ordinal);
        Assert.Contains("\"ActiveScenarioWriterHandledAtUtc\"", script, StringComparison.Ordinal);
        Assert.Contains("\"LastAppliedStageId\" = CASE", script, StringComparison.Ordinal);
        Assert.Contains("\"LastAppliedResponsibleId\" = CASE", script, StringComparison.Ordinal);
        Assert.Contains("\"LastAssignmentId\" = assignment.\"Id\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "assignment.\"DealAppliedAtUtc\" IS NOT NULL",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "assignment.\"OperationMode\" = 'shadow' AND assignment.\"AppliedAtUtc\" IS NOT NULL",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_ForcesLegacyShadowAndWriterConfigurationsBackToUnconfirmedShadow()
    {
        var script = NormalizeSql(GenerateUpScript());

        Assert.Contains(
            "UPDATE \"BitrixWorkforceConfigurations\" " +
            "SET \"OperationMode\" = 'shadow', " +
            "\"WriterRulesConfirmed\" = FALSE, " +
            "\"UpdatedAtUtc\" = CURRENT_TIMESTAMP " +
            "WHERE \"OperationMode\" IN ('shadow', 'writer')",
            script,
            StringComparison.Ordinal);
    }

    private static string GenerateUpScript()
    {
        using var db = CreateDb();

        return db.GetService<IMigrator>().GenerateScript(PreviousMigrationId, MigrationId);
    }

    private static OrbitaDbContext CreateDb() =>
        new(
            new DbContextOptionsBuilder<OrbitaDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=orbita_migration_metadata;Username=orbita;Password=orbita")
                .Options);

    private static string NormalizeSql(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }
}
