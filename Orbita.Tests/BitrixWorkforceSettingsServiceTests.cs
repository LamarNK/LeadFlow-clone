using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixWorkforceSettingsServiceTests
{
    [Fact]
    public async Task WriterGate_RequiresIdenticalConfigurationPreviouslySavedInShadow()
    {
        var client = new SettingsBitrixClientStub();
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        var sut = context.Sut;
        var instanceId = context.InstanceId;
        var scope = context.Scope;

        var (_, disabledError) = await sut.SaveAsync(
            instanceId,
            scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.DisabledMode),
            "admin");
        Assert.Null(disabledError);

        var (rejectedWriter, gateError) = await sut.SaveAsync(
            instanceId,
            scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");
        Assert.Null(rejectedWriter);
        Assert.Contains("shadow", gateError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.ValidateCrmAccessCalls);

        var (shadow, shadowError) = await sut.SaveAsync(
            instanceId,
            scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.ShadowMode),
            "admin");
        Assert.Null(shadowError);
        Assert.Equal(BitrixWorkforceDistribution.ShadowMode, shadow!.OperationMode);

        var (writer, writerError) = await sut.SaveAsync(
            instanceId,
            scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");
        Assert.Null(writerError);
        Assert.Equal(BitrixWorkforceDistribution.WriterMode, writer!.OperationMode);
        Assert.True(writer.WriterRulesConfirmed);
        Assert.Equal(1, client.ValidateDealFieldsCalls);
        Assert.Equal(
            ["UF_IDEMPOTENCY", "UF_AGE", "UF_PROFESSION", "UF_CITY"],
            client.LastRequiredFieldCodes);
        Assert.Equal(1, client.ValidateDealPipelineCalls);
        Assert.Equal(1, client.GetManagerStatusesCalls);
        Assert.Equal([10L, 20L], client.LastManagerIds);
        Assert.Equal(0, client.LastCategoryId);
        Assert.Equal(["NEW", "IN_PROCESS"], client.LastStageIds);
    }

    [Fact]
    public async Task WriterGate_RejectsSecondEnabledWriterForSamePortalAndCategory()
    {
        var client = new SettingsBitrixClientStub();
        var context = await CreateContextAsync(client);
        await using var db = context.Db;

        var (_, shadowError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.ShadowMode),
            "admin");
        Assert.Null(shadowError);
        await AddOtherWriterAsync(
            db,
            context.InstanceId,
            dealCategoryId: 0,
            instanceIsEnabled: true);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("второй writer", writerError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.ValidateDealFieldsCalls);
        Assert.Equal(
            BitrixWorkforceDistribution.ShadowMode,
            (await db.BitrixWorkforceConfigurations
                .SingleAsync(x => x.BitrixInstanceId == context.InstanceId))
            .OperationMode);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task WriterGate_AllowsWriterWhenOtherPortalBindingDoesNotConflict(
        int otherDealCategoryId,
        bool otherInstanceIsEnabled)
    {
        var client = new SettingsBitrixClientStub();
        var context = await CreateContextAsync(client);
        await using var db = context.Db;

        var (_, shadowError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.ShadowMode),
            "admin");
        Assert.Null(shadowError);
        await AddOtherWriterAsync(
            db,
            context.InstanceId,
            otherDealCategoryId,
            otherInstanceIsEnabled);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writerError);
        Assert.NotNull(writer);
        Assert.Equal(BitrixWorkforceDistribution.WriterMode, writer.OperationMode);
        Assert.Equal(1, client.ValidateDealFieldsCalls);
    }

    [Fact]
    public async Task WriterGate_RejectsWriterWhenBitrixInstanceIsDisabled()
    {
        var client = new SettingsBitrixClientStub();
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        await SaveShadowAndGetRevisionAsync(context);
        var instance = await db.BitrixInstances.SingleAsync(x => x.Id == context.InstanceId);
        instance.IsEnabled = false;
        await db.SaveChangesAsync();

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("отключено", writerError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.ValidateDealFieldsCalls);
        Assert.Equal(0, client.ListDealsCalls);
        Assert.Equal(
            BitrixWorkforceDistribution.ShadowMode,
            (await db.BitrixWorkforceConfigurations.SingleAsync()).OperationMode);
    }

    [Fact]
    public async Task ShadowCoverageGate_RejectsCurrentDealWithoutState()
    {
        var client = new SettingsBitrixClientStub
        {
            CurrentDeals = [new BitrixWorkforceDealRevision(501, "NEW", "revision-501")]
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        await SaveShadowAndGetRevisionAsync(context);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("501", writerError, StringComparison.Ordinal);
        Assert.Equal(1, client.ListDealsCalls);
        Assert.Equal(0, client.LastListDealsCategoryId);
        Assert.Equal(["NEW"], client.LastListDealsStageIds);
        Assert.Equal(
            BitrixWorkforceDistribution.ShadowMode,
            (await db.BitrixWorkforceConfigurations.SingleAsync()).OperationMode);
    }

    [Theory]
    [InlineData("other_instance")]
    [InlineData("stage_mismatch")]
    [InlineData("scenario_case_mismatch")]
    [InlineData("null_markers")]
    [InlineData("stale_marker")]
    public async Task ShadowCoverageGate_RejectsMismatchedOrUnobservedState(string stateKind)
    {
        const long dealId = 502;
        var client = new SettingsBitrixClientStub
        {
            CurrentDeals = [new BitrixWorkforceDealRevision(dealId, "NEW", "revision-502")]
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        var shadowRevision = await SaveShadowAndGetRevisionAsync(context);
        db.BitrixWorkforceDealStates.Add(new BitrixWorkforceDealStateEntity
        {
            BitrixInstanceId = stateKind == "other_instance"
                ? Guid.NewGuid()
                : context.InstanceId,
            DealId = dealId,
            LastObservedStageId = stateKind == "stage_mismatch" ? "OTHER" : "NEW",
            ActiveScenario = stateKind == "scenario_case_mismatch"
                ? BitrixWorkforceDistribution.NewScenario.ToUpperInvariant()
                : BitrixWorkforceDistribution.NewScenario,
            ActiveScenarioShadowHandledAtUtc = stateKind switch
            {
                "null_markers" => null,
                "stale_marker" => shadowRevision.AddSeconds(-1),
                _ => shadowRevision
            },
            ActiveScenarioWriterHandledAtUtc = null,
            UpdatedAtUtc = shadowRevision
        });
        await db.SaveChangesAsync();

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains(dealId.ToString(), writerError, StringComparison.Ordinal);
        Assert.Equal(
            BitrixWorkforceDistribution.ShadowMode,
            (await db.BitrixWorkforceConfigurations.SingleAsync()).OperationMode);
    }

    [Fact]
    public async Task ShadowCoverageGate_RejectsMixedCoveredAndUncoveredDeals()
    {
        var client = new SettingsBitrixClientStub
        {
            CurrentDeals =
            [
                new BitrixWorkforceDealRevision(503, "NEW", "revision-503"),
                new BitrixWorkforceDealRevision(504, "NEW", "revision-504")
            ]
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        var shadowRevision = await SaveShadowAndGetRevisionAsync(context);
        await AddCoverageStateAsync(
            db,
            context.InstanceId,
            dealId: 503,
            lastObservedStageId: "NEW",
            activeScenario: BitrixWorkforceDistribution.NewScenario,
            shadowHandledAtUtc: shadowRevision,
            writerHandledAtUtc: null);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("504", writerError, StringComparison.Ordinal);
        Assert.DoesNotContain("503", writerError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShadowCoverageGate_AllowsEmptyCurrentDealSet()
    {
        var client = new SettingsBitrixClientStub { CurrentDeals = [] };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        await SaveShadowAndGetRevisionAsync(context);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writerError);
        Assert.Equal(BitrixWorkforceDistribution.WriterMode, writer!.OperationMode);
        Assert.Equal(2, client.ListDealsCalls);
    }

    [Fact]
    public async Task ShadowCoverageGate_AllowsMatchingShadowMarkerAndComparesStageIgnoringCase()
    {
        const long dealId = 505;
        var client = new SettingsBitrixClientStub
        {
            CurrentDeals = [new BitrixWorkforceDealRevision(dealId, "new", "revision-505")]
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        var shadowRevision = await SaveShadowAndGetRevisionAsync(context);
        await AddCoverageStateAsync(
            db,
            context.InstanceId,
            dealId,
            lastObservedStageId: "NeW",
            activeScenario: BitrixWorkforceDistribution.NewScenario,
            shadowHandledAtUtc: shadowRevision,
            writerHandledAtUtc: null);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writerError);
        Assert.Equal(BitrixWorkforceDistribution.WriterMode, writer!.OperationMode);
        Assert.Equal(2, client.ListDealsCalls);
    }

    [Fact]
    public async Task ShadowCoverageGate_AllowsMatchingWriterMarker()
    {
        const long dealId = 506;
        var client = new SettingsBitrixClientStub
        {
            CurrentDeals = [new BitrixWorkforceDealRevision(dealId, "NEW", "revision-506")]
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        var shadowRevision = await SaveShadowAndGetRevisionAsync(context);
        await AddCoverageStateAsync(
            db,
            context.InstanceId,
            dealId,
            lastObservedStageId: "NEW",
            activeScenario: BitrixWorkforceDistribution.NewScenario,
            shadowHandledAtUtc: null,
            writerHandledAtUtc: shadowRevision);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writerError);
        Assert.Equal(BitrixWorkforceDistribution.WriterMode, writer!.OperationMode);
        Assert.Equal(2, client.ListDealsCalls);
    }

    [Fact]
    public async Task ShadowCoverageGate_RejectsDealChangedBetweenVerificationScans()
    {
        const long dealId = 507;
        var client = new SettingsBitrixClientStub
        {
            ListDealsFactory = call =>
            [
                new BitrixWorkforceDealRevision(
                    dealId,
                    "NEW",
                    call == 1 ? "revision-before" : "revision-after")
            ]
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        var shadowRevision = await SaveShadowAndGetRevisionAsync(context);
        await AddCoverageStateAsync(
            db,
            context.InstanceId,
            dealId,
            lastObservedStageId: "NEW",
            activeScenario: BitrixWorkforceDistribution.NewScenario,
            shadowHandledAtUtc: shadowRevision,
            writerHandledAtUtc: null);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("измен", writerError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, client.ListDealsCalls);
        Assert.Equal(
            BitrixWorkforceDistribution.ShadowMode,
            (await db.BitrixWorkforceConfigurations.SingleAsync()).OperationMode);
    }

    [Fact]
    public async Task ShadowCoverageGate_ReportsListDealsFailureAndKeepsShadow()
    {
        var client = new SettingsBitrixClientStub();
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        await SaveShadowAndGetRevisionAsync(context);
        client.ListDealsError = new InvalidOperationException("coverage list failed");

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("coverage list failed", writerError, StringComparison.Ordinal);
        Assert.Equal(1, client.ListDealsCalls);
        Assert.Equal(
            BitrixWorkforceDistribution.ShadowMode,
            (await db.BitrixWorkforceConfigurations.SingleAsync()).OperationMode);
    }

    [Fact]
    public async Task WriterGate_AllowsClosedManagerButRejectsMissingOrInactiveManager()
    {
        var client = new SettingsBitrixClientStub
        {
            ManagerStatusesFactory = managerIds =>
            [
                new BitrixWorkforceManagerStatus(
                    managerIds[0],
                    IsActive: true,
                    TimemanStatus: "CLOSED"),
                new BitrixWorkforceManagerStatus(
                    managerIds[1],
                    IsActive: false,
                    TimemanStatus: null)
            ]
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;

        var (_, shadowError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.ShadowMode),
            "admin");
        Assert.Null(shadowError);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("20", writerError, StringComparison.Ordinal);
        Assert.Contains("неактив", writerError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([10L, 20L], client.LastManagerIds);
        Assert.Equal(
            BitrixWorkforceDistribution.ShadowMode,
            (await db.BitrixWorkforceConfigurations.SingleAsync()).OperationMode);
    }

    [Fact]
    public async Task WriterGate_RejectsPipelineAccessErrorBeforeCheckingManagers()
    {
        var client = new SettingsBitrixClientStub
        {
            PipelineValidationError = new InvalidOperationException("stage MISSING was not found")
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;

        var (_, shadowError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.ShadowMode),
            "admin");
        Assert.Null(shadowError);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("MISSING", writerError, StringComparison.Ordinal);
        Assert.Equal(1, client.ValidateDealPipelineCalls);
        Assert.Equal(0, client.GetManagerStatusesCalls);
    }

    [Fact]
    public async Task WriterGate_RejectsMissingPerInstanceDealFieldBeforePipelineCheck()
    {
        var client = new SettingsBitrixClientStub
        {
            DealFieldValidationError = new InvalidOperationException(
                "deal field UF_CITY was not found")
        };
        var context = await CreateContextAsync(client);
        await using var db = context.Db;

        var (_, shadowError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.ShadowMode),
            "admin");
        Assert.Null(shadowError);

        var (writer, writerError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(
                BitrixWorkforceDistribution.WriterMode,
                writerRulesConfirmed: true),
            "admin");

        Assert.Null(writer);
        Assert.Contains("UF_CITY", writerError, StringComparison.Ordinal);
        Assert.Equal(1, client.ValidateDealFieldsCalls);
        Assert.Equal(0, client.ValidateDealPipelineCalls);
        Assert.Equal(0, client.GetManagerStatusesCalls);
    }

    [Fact]
    public async Task Save_RejectsEnabledTargetThatIsSourceOfAnotherScenario()
    {
        var client = new SettingsBitrixClientStub();
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        var request = CreateRequest(BitrixWorkforceDistribution.ShadowMode) with
        {
            StageRules =
            [
                new BitrixWorkforceStageRuleDto(
                    Id: null,
                    Scenario: BitrixWorkforceDistribution.MissedCallScenario,
                    SourceStageId: "UC_FIRST",
                    TargetStageId: "UC_HANDOFF",
                    UsesMorningWindow: false,
                    SortOrder: 0,
                    IsEnabled: true),
                new BitrixWorkforceStageRuleDto(
                    Id: null,
                    Scenario: BitrixWorkforceDistribution.SubstituteMissedCallScenario,
                    SourceStageId: "uc_handoff",
                    TargetStageId: "UC_SECOND",
                    UsesMorningWindow: false,
                    SortOrder: 1,
                    IsEnabled: true)
            ]
        };

        var (settings, error) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            request,
            "admin");

        Assert.Null(settings);
        Assert.Contains("Target", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Source", error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.BitrixWorkforceConfigurations.ToListAsync());
        Assert.Empty(await db.BitrixWorkforceStageRules.ToListAsync());
    }

    [Fact]
    public async Task Save_AllowsTargetToSourceHandoffInsideSameScenario()
    {
        var client = new SettingsBitrixClientStub();
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        var request = CreateRequest(BitrixWorkforceDistribution.ShadowMode) with
        {
            StageRules =
            [
                new BitrixWorkforceStageRuleDto(
                    Id: null,
                    Scenario: BitrixWorkforceDistribution.MissedCallScenario,
                    SourceStageId: "UC_FIRST",
                    TargetStageId: "UC_HANDOFF",
                    UsesMorningWindow: false,
                    SortOrder: 0,
                    IsEnabled: true),
                new BitrixWorkforceStageRuleDto(
                    Id: null,
                    Scenario: BitrixWorkforceDistribution.MissedCallScenario,
                    SourceStageId: "UC_HANDOFF",
                    TargetStageId: "UC_HANDOFF",
                    UsesMorningWindow: false,
                    SortOrder: 1,
                    IsEnabled: true)
            ]
        };

        var (settings, error) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            request,
            "admin");

        Assert.Null(error);
        Assert.NotNull(settings);
        Assert.Equal(2, await db.BitrixWorkforceStageRules.CountAsync());
    }

    [Fact]
    public async Task Save_IgnoresCrossScenarioCollisionWhenSourceRuleIsDisabled()
    {
        var client = new SettingsBitrixClientStub();
        var context = await CreateContextAsync(client);
        await using var db = context.Db;
        var request = CreateRequest(BitrixWorkforceDistribution.ShadowMode) with
        {
            StageRules =
            [
                new BitrixWorkforceStageRuleDto(
                    Id: null,
                    Scenario: BitrixWorkforceDistribution.MissedCallScenario,
                    SourceStageId: "UC_FIRST",
                    TargetStageId: "UC_HANDOFF",
                    UsesMorningWindow: false,
                    SortOrder: 0,
                    IsEnabled: true),
                new BitrixWorkforceStageRuleDto(
                    Id: null,
                    Scenario: BitrixWorkforceDistribution.SubstituteMissedCallScenario,
                    SourceStageId: "UC_HANDOFF",
                    TargetStageId: "UC_SECOND",
                    UsesMorningWindow: false,
                    SortOrder: 1,
                    IsEnabled: false)
            ]
        };

        var (settings, error) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            request,
            "admin");

        Assert.Null(error);
        Assert.NotNull(settings);
        Assert.Equal(2, await db.BitrixWorkforceStageRules.CountAsync());
    }

    private static UpdateBitrixWorkforceSettingsRequest CreateRequest(
        string mode,
        bool writerRulesConfirmed = false) =>
        new(
            mode,
            DealCategoryId: 0,
            TimeZoneId: "Europe/Moscow",
            ManagerUserIds: [10, 20],
            StageRules:
            [
                new BitrixWorkforceStageRuleDto(
                    Id: null,
                    Scenario: BitrixWorkforceDistribution.NewScenario,
                    SourceStageId: "NEW",
                    TargetStageId: "IN_PROCESS",
                    UsesMorningWindow: false,
                    SortOrder: 0,
                    IsEnabled: true),
                new BitrixWorkforceStageRuleDto(
                    Id: null,
                    Scenario: BitrixWorkforceDistribution.MissedCallScenario,
                    SourceStageId: "DISABLED_SOURCE",
                    TargetStageId: "DISABLED_TARGET",
                    UsesMorningWindow: false,
                    SortOrder: 1,
                    IsEnabled: false)
            ],
            WriterRulesConfirmed: writerRulesConfirmed);

    private static async Task<DateTime> SaveShadowAndGetRevisionAsync(
        SettingsTestContext context)
    {
        var (_, shadowError) = await context.Sut.SaveAsync(
            context.InstanceId,
            context.Scope,
            officeId: null,
            CreateRequest(BitrixWorkforceDistribution.ShadowMode),
            "admin");
        Assert.Null(shadowError);
        return await context.Db.BitrixWorkforceConfigurations
            .AsNoTracking()
            .Where(x => x.BitrixInstanceId == context.InstanceId)
            .Select(x => x.UpdatedAtUtc)
            .SingleAsync();
    }

    private static async Task AddCoverageStateAsync(
        OrbitaDbContext db,
        Guid bitrixInstanceId,
        long dealId,
        string? lastObservedStageId,
        string? activeScenario,
        DateTime? shadowHandledAtUtc,
        DateTime? writerHandledAtUtc)
    {
        db.BitrixWorkforceDealStates.Add(new BitrixWorkforceDealStateEntity
        {
            BitrixInstanceId = bitrixInstanceId,
            DealId = dealId,
            LastObservedStageId = lastObservedStageId,
            ActiveScenario = activeScenario,
            ActiveScenarioShadowHandledAtUtc = shadowHandledAtUtc,
            ActiveScenarioWriterHandledAtUtc = writerHandledAtUtc,
            UpdatedAtUtc = shadowHandledAtUtc ?? writerHandledAtUtc ?? DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task AddOtherWriterAsync(
        OrbitaDbContext db,
        Guid currentInstanceId,
        int dealCategoryId,
        bool instanceIsEnabled)
    {
        var currentInstance = await db.BitrixInstances
            .AsNoTracking()
            .SingleAsync(x => x.Id == currentInstanceId);
        var otherInstanceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = otherInstanceId,
            OfficeId = currentInstance.OfficeId,
            Name = "Other Bitrix",
            PortalHost = currentInstance.PortalHost,
            // Exercise the stored PortalHost fallback used for legacy instances
            // whose encrypted webhook is absent.
            WebhookUrlProtected = string.Empty,
            IntegrationSettingsJson = "{}",
            IsEnabled = instanceIsEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        db.BitrixWorkforceConfigurations.Add(new BitrixWorkforceConfigurationEntity
        {
            BitrixInstanceId = otherInstanceId,
            OperationMode = BitrixWorkforceDistribution.WriterMode,
            DealCategoryId = dealCategoryId,
            WriterRulesConfirmed = true,
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();
    }

    private static async Task<SettingsTestContext> CreateContextAsync(
        SettingsBitrixClientStub client)
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new OrbitaDbContext(options);
        var officeId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var protector = new WebhookSecretProtector(
            new EphemeralDataProtectionProvider());
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.BitrixInstances.Add(new BitrixInstanceEntity
        {
            Id = instanceId,
            OfficeId = officeId,
            Name = "Bitrix",
            PortalHost = "example.bitrix24.ru",
            WebhookUrlProtected = protector.Protect(
                "https://example.bitrix24.ru/rest/1/secret"),
            IntegrationSettingsJson = new BitrixInstanceIntegrationSettings
            {
                ResponsibleId = 999,
                DealIdempotencyUfCode = "UF_IDEMPOTENCY",
                DealAgeUfCode = "UF_AGE",
                DealProfessionUfCode = "UF_PROFESSION",
                DealCityUfCode = "UF_CITY"
            }.Serialize(),
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var instances = new BitrixInstanceService(
            db,
            protector,
            null!,
            Options.Create(new OrbitaBitrixSettings()),
            new PanelAuditService(db));
        var sut = new BitrixWorkforceSettingsService(
            db,
            Options.Create(new BitrixWorkforceOptions()),
            Options.Create(new OrbitaBitrixSettings()),
            instances,
            client);
        return new SettingsTestContext(
            db,
            sut,
            instanceId,
            OfficeScope.ForOffice(officeId));
    }

    private sealed record SettingsTestContext(
        OrbitaDbContext Db,
        BitrixWorkforceSettingsService Sut,
        Guid InstanceId,
        OfficeScope Scope);

    private sealed class SettingsBitrixClientStub : IBitrixWorkforceClient
    {
        public int ValidateCrmAccessCalls { get; private set; }
        public int ValidateDealFieldsCalls { get; private set; }
        public int ValidateDealPipelineCalls { get; private set; }
        public int GetManagerStatusesCalls { get; private set; }
        public int ListDealsCalls { get; private set; }
        public IReadOnlyList<long> LastManagerIds { get; private set; } = [];
        public int? LastCategoryId { get; private set; }
        public IReadOnlyList<string> LastStageIds { get; private set; } = [];
        public IReadOnlyList<string> LastRequiredFieldCodes { get; private set; } = [];
        public int? LastListDealsCategoryId { get; private set; }
        public IReadOnlyList<string> LastListDealsStageIds { get; private set; } = [];
        public Exception? DealFieldValidationError { get; init; }
        public Exception? PipelineValidationError { get; init; }
        public Exception? ListDealsError { get; set; }
        public IReadOnlyList<BitrixWorkforceDealRevision> CurrentDeals { get; set; } = [];
        public Func<int, IReadOnlyList<BitrixWorkforceDealRevision>>? ListDealsFactory
        {
            get;
            set;
        }
        public Func<IReadOnlyList<long>, IReadOnlyList<BitrixWorkforceManagerStatus>>?
            ManagerStatusesFactory { get; init; }

        public Task ValidateCrmAccessAsync(
            string webhookUrl,
            CancellationToken ct)
        {
            ValidateCrmAccessCalls++;
            return Task.CompletedTask;
        }

        public Task ValidateDealFieldsAsync(
            string webhookUrl,
            IReadOnlyCollection<string> requiredFieldCodes,
            CancellationToken ct)
        {
            ValidateDealFieldsCalls++;
            LastRequiredFieldCodes = requiredFieldCodes.ToList();
            return DealFieldValidationError is null
                ? Task.CompletedTask
                : Task.FromException(DealFieldValidationError);
        }

        public Task<IReadOnlyList<BitrixWorkforceManagerStatus>> GetManagerStatusesAsync(
            string webhookUrl,
            IReadOnlyList<long> managerIds,
            CancellationToken ct)
        {
            GetManagerStatusesCalls++;
            LastManagerIds = managerIds.ToList();
            if (ManagerStatusesFactory is not null)
            {
                return Task.FromResult(ManagerStatusesFactory(managerIds));
            }

            return Task.FromResult<IReadOnlyList<BitrixWorkforceManagerStatus>>(
                managerIds.Select((x, index) => new BitrixWorkforceManagerStatus(
                    x,
                    IsActive: true,
                    TimemanStatus: index == 0
                        ? BitrixWorkforceDistribution.TimemanOpened
                        : "CLOSED")).ToList());
        }

        public Task ValidateDealPipelineAsync(
            string webhookUrl,
            int categoryId,
            IReadOnlyCollection<string> stageIds,
            CancellationToken ct)
        {
            ValidateDealPipelineCalls++;
            LastCategoryId = categoryId;
            LastStageIds = stageIds.ToList();
            return PipelineValidationError is null
                ? Task.CompletedTask
                : Task.FromException(PipelineValidationError);
        }

        public Task<BitrixWorkforceDeal> GetDealAsync(
            string webhookUrl,
            long dealId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> GetDealContactIdsAsync(
            string webhookUrl,
            long dealId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long?> GetContactOwnerIdAsync(
            string webhookUrl,
            long contactId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateDealAsync(
            string webhookUrl,
            long dealId,
            IReadOnlyDictionary<string, object?> fields,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateContactOwnerAsync(
            string webhookUrl,
            long contactId,
            long responsibleId,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BitrixWorkforceDealRevision>> ListDealsAsync(
            string webhookUrl,
            int categoryId,
            IReadOnlyCollection<string> stageIds,
            CancellationToken ct)
        {
            ListDealsCalls++;
            LastListDealsCategoryId = categoryId;
            LastListDealsStageIds = stageIds.ToList();
            return ListDealsError is null
                ? Task.FromResult(ListDealsFactory?.Invoke(ListDealsCalls) ?? CurrentDeals)
                : Task.FromException<IReadOnlyList<BitrixWorkforceDealRevision>>(
                    ListDealsError);
        }
    }
}
