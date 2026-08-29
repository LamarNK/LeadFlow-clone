using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Api.Services;
using Orbita.Contracts;
using System.Text.Json;

namespace Orbita.Tests;

public sealed class TelemetryServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task SaveSnapshotAsync_NullSubProfiles_DoesNotWipeExistingJson()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");
        const string existingJson = """[{"Id":"sp-1","Name":"Alpha"}]""";
        var account = await db.WorkerAccounts.SingleAsync();
        account.SubProfilesJson = existingJson;
        await db.SaveChangesAsync();

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var capturedAt = DateTime.UtcNow;
        var request = new WorkerSnapshotRequest(
            WorkerId,
            capturedAt,
            CreateNonEmptyStats(),
            [
                new WorkerAccountDto(
                    AccountId,
                    "acc-1",
                    "Ok",
                    true,
                    1,
                    0,
                    0,
                    null,
                    capturedAt,
                    SubProfiles: null)
            ],
            [
                new WorkerBalanceDto(AccountId, "acc-1", 0m, [])
            ]);

        var saved = await sut.SaveSnapshotAsync(request, CancellationToken.None);

        Assert.True(saved);
        account = await db.WorkerAccounts.SingleAsync();
        Assert.Equal(existingJson, account.SubProfilesJson);
    }

    [Fact]
    public async Task SaveSnapshotAsync_UnspecifiedSubProfilesRefreshedAt_SavesSuccessfully()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var capturedAt = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var request = new WorkerSnapshotRequest(
            WorkerId,
            capturedAt,
            CreateNonEmptyStats(),
            [
                new WorkerAccountDto(
                    AccountId,
                    "acc-1",
                    "Ok",
                    true,
                    1,
                    0,
                    0,
                    null,
                    LastMonitoringAt: new DateTime(2026, 7, 1, 11, 0, 0, DateTimeKind.Unspecified),
                    SubProfiles:
                    [
                        new WorkerSubProfileDto("sp-1", "Alpha", "", true, 100m, null, null, null)
                    ],
                    SubProfilesRefreshedAtUtc: new DateTime(2026, 7, 1, 11, 30, 0, DateTimeKind.Unspecified))
            ],
            [
                new WorkerBalanceDto(AccountId, "acc-1", 100m, [])
            ]);

        var saved = await sut.SaveSnapshotAsync(request, CancellationToken.None);

        Assert.True(saved);
        var account = await db.WorkerAccounts.SingleAsync();
        Assert.NotNull(account.SubProfilesRefreshedAtUtc);
        Assert.Equal(DateTimeKind.Utc, account.SubProfilesRefreshedAtUtc!.Value.Kind);
        Assert.NotNull(account.LastMonitoringAt);
        Assert.Equal(DateTimeKind.Utc, account.LastMonitoringAt!.Value.Kind);
    }

    [Fact]
    public async Task SaveActivityAsync_UpdatesWorkerAndNotifiesOnChange()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");

        var notifier = new CapturingPanelRealtimeNotifier();
        var sut = new TelemetryService(db, new OfficeAdminService(db), notifier);
        var request = new WorkerActivityRequest(
            WorkerId,
            WorkerActivityPhases.SubProfile,
            "сбор откликов",
            AccountId,
            "acc-1",
            "sp-1",
            "Основной",
            UpdatedAtUtc: DateTime.UtcNow);

        var saved = await sut.SaveActivityAsync(request, CancellationToken.None);

        Assert.True(saved);
        var worker = await db.Workers.SingleAsync();
        Assert.Equal(WorkerActivityPhases.SubProfile, worker.ActivityPhase);
        Assert.Equal("сбор откликов", worker.ActivityMessage);
        Assert.Equal(AccountId, worker.ActivityAccountId);
        Assert.Equal("sp-1", worker.ActivitySubProfileId);
        Assert.NotNull(worker.ActivityUpdatedAtUtc);
        Assert.Equal(1, notifier.NotifyCount);
        Assert.Equal(WorkerId, notifier.LastWorkerId);
        Assert.Contains(PanelChangeKind.Workers, notifier.LastKinds);
        Assert.Contains(PanelChangeKind.Dashboard, notifier.LastKinds);
        Assert.Contains(PanelChangeKind.Accounts, notifier.LastKinds);
    }

    [Fact]
    public async Task SaveActivityAsync_DoesNotNotifyWhenUnchanged()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");
        var worker = await db.Workers.SingleAsync();
        worker.ActivityPhase = WorkerActivityPhases.Cycle;
        worker.ActivityMessage = "старт цикла";
        worker.ActivityUpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var notifier = new CapturingPanelRealtimeNotifier();
        var sut = new TelemetryService(db, new OfficeAdminService(db), notifier);
        var request = new WorkerActivityRequest(
            WorkerId,
            WorkerActivityPhases.Cycle,
            "старт цикла",
            UpdatedAtUtc: DateTime.UtcNow);

        var saved = await sut.SaveActivityAsync(request, CancellationToken.None);

        Assert.True(saved);
        Assert.Equal(0, notifier.NotifyCount);
    }

    [Fact]
    public async Task HeartbeatAsync_UsesReportedPublicIp_WhenConnectionIpIsLoopback()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var request = new WorkerHeartbeatRequest(
            WorkerId,
            "worker-1",
            "1.0.0.23",
            "VM-0D94DA08-DB2",
            "Running",
            null,
            true,
            DateTime.UtcNow.AddMinutes(5),
            PublicIpAddress: "46.146.232.119");

        var saved = await sut.HeartbeatAsync(request, "127.0.0.1", CancellationToken.None);

        Assert.True(saved);
        var worker = await db.Workers.SingleAsync();
        Assert.Equal("46.146.232.119", worker.IpAddress);
    }

    [Fact]
    public async Task SaveActivityAsync_ReturnsFalseForUnknownWorker()
    {
        await using var db = CreateDb();
        var sut = new TelemetryService(db, new OfficeAdminService(db), new CapturingPanelRealtimeNotifier());
        var request = new WorkerActivityRequest(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            WorkerActivityPhases.Idle,
            "ожидание");

        var saved = await sut.SaveActivityAsync(request, CancellationToken.None);

        Assert.False(saved);
    }

    [Fact]
    public async Task SaveEventsAsync_SkipsDuplicateActiveEvent()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");
        const string message = "Аккаунт «acc-1» — Капча / блок IP: требуется действие";
        db.WorkerEvents.Add(new WorkerEventEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = WorkerId,
            AccountId = AccountId,
            Level = "Warning",
            Message = message,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        await db.SaveChangesAsync();

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var request = new WorkerEventBatchRequest(
            WorkerId,
            [
                new WorkerEventDto(
                    AccountId,
                    "Warning",
                    message,
                    """{"attachmentId":"11111111-1111-1111-1111-111111111111"}""",
                    DateTime.UtcNow)
            ]);

        var saved = await sut.SaveEventsAsync(request, CancellationToken.None);

        Assert.True(saved);
        Assert.Equal(1, await db.WorkerEvents.CountAsync());
    }

    [Fact]
    public async Task SaveEventsAsync_SkipsEventAfterDismiss()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");
        const string message = "Аккаунт «acc-1» — Нужен вход: сессия истекла";
        db.WorkerEvents.Add(new WorkerEventEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = WorkerId,
            AccountId = AccountId,
            Level = "Warning",
            Message = message,
            CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
            IsDismissed = true,
            DismissedAtUtc = DateTime.UtcNow.AddMinutes(-30)
        });
        await db.SaveChangesAsync();

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var request = new WorkerEventBatchRequest(
            WorkerId,
            [
                new WorkerEventDto(
                    AccountId,
                    "Warning",
                    message,
                    """{"attachmentId":"22222222-2222-2222-2222-222222222222"}""",
                    DateTime.UtcNow)
            ]);

        var saved = await sut.SaveEventsAsync(request, CancellationToken.None);

        Assert.True(saved);
        Assert.Equal(1, await db.WorkerEvents.CountAsync());
        Assert.True((await db.WorkerEvents.SingleAsync()).IsDismissed);
    }

    [Fact]
    public async Task SaveEventsAsync_InsertsWhenFingerprintDiffers()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");
        db.WorkerEvents.Add(new WorkerEventEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = WorkerId,
            AccountId = AccountId,
            Level = "Warning",
            Message = "Аккаунт «acc-1» — Нужен вход: сессия истекла",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
            IsDismissed = true,
            DismissedAtUtc = DateTime.UtcNow.AddMinutes(-30)
        });
        await db.SaveChangesAsync();

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var request = new WorkerEventBatchRequest(
            WorkerId,
            [
                new WorkerEventDto(
                    AccountId,
                    "Error",
                    "Ошибка аккаунта acc-1: timeout",
                    "timeout",
                    DateTime.UtcNow)
            ]);

        var saved = await sut.SaveEventsAsync(request, CancellationToken.None);

        Assert.True(saved);
        Assert.Equal(2, await db.WorkerEvents.CountAsync());
    }

    [Fact]
    public async Task SaveSnapshotAsync_DoesNotWipeSubProfiles_WithPlaceholderIncoming()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");
        const string existingJson = """
            [
              {"Id":"sp-1","Name":"Alpha","Balance":4200,"WalletBalance":100},
              {"Id":"sp-2","Name":"Beta","Balance":800,"WalletBalance":0}
            ]
            """;
        var account = await db.WorkerAccounts.SingleAsync();
        account.SubProfilesJson = existingJson;
        await db.SaveChangesAsync();

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var capturedAt = DateTime.UtcNow;
        var request = new WorkerSnapshotRequest(
            WorkerId,
            capturedAt,
            CreateNonEmptyStats(),
            [
                new WorkerAccountDto(
                    AccountId,
                    "acc-1",
                    "Ok",
                    true,
                    1,
                    0,
                    0,
                    null,
                    capturedAt,
                    SubProfiles:
                    [
                        new WorkerSubProfileDto("—", "—", "", false, null, null, null, null)
                    ])
            ],
            [
                new WorkerBalanceDto(AccountId, "acc-1", 0m, [new SubProfileBalanceDto("—", null)])
            ]);

        var saved = await sut.SaveSnapshotAsync(request, CancellationToken.None);

        Assert.True(saved);
        account = await db.WorkerAccounts.SingleAsync();
        Assert.Equal(existingJson, account.SubProfilesJson);
    }

    [Fact]
    public async Task SaveSnapshotAsync_DoesNotWipeSubProfiles_WithEmptyArrayIncoming()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");
        const string existingJson = """[{"Id":"sp-1","Name":"Alpha","Balance":4200}]""";
        var account = await db.WorkerAccounts.SingleAsync();
        account.SubProfilesJson = existingJson;
        await db.SaveChangesAsync();

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var capturedAt = DateTime.UtcNow;
        var request = new WorkerSnapshotRequest(
            WorkerId,
            capturedAt,
            CreateNonEmptyStats(),
            [
                new WorkerAccountDto(
                    AccountId,
                    "acc-1",
                    "Ok",
                    true,
                    1,
                    0,
                    0,
                    null,
                    capturedAt,
                    SubProfiles: [])
            ],
            [
                new WorkerBalanceDto(AccountId, "acc-1", 0m, [])
            ]);

        var saved = await sut.SaveSnapshotAsync(request, CancellationToken.None);

        Assert.True(saved);
        account = await db.WorkerAccounts.SingleAsync();
        Assert.Equal(existingJson, account.SubProfilesJson);
    }

    [Fact]
    public async Task SaveSnapshotAsync_PreservesExistingBalance_WhenIncomingSnapshotHasNoBalanceData()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");
        var account = await db.WorkerAccounts.SingleAsync();
        account.TotalBalance = 12500m;
        account.SubProfilesJson = JsonSerializer.Serialize(new[]
        {
            new WorkerSubProfileDto("sp-1", "Alpha", "", true, 12500m, null, null, null, WalletBalance: 500m, AdvanceDurationText: "~ на 10 дней")
        });
        await db.SaveChangesAsync();

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var capturedAt = DateTime.UtcNow;
        var request = new WorkerSnapshotRequest(
            WorkerId,
            capturedAt,
            CreateNonEmptyStats(),
            [
                new WorkerAccountDto(
                    AccountId,
                    "acc-1",
                    "Ok",
                    true,
                    1,
                    0,
                    0,
                    null,
                    capturedAt,
                    SubProfiles:
                    [
                        new WorkerSubProfileDto("sp-1", "Alpha", "", true, null, null, null, null)
                    ])
            ],
            [
                new WorkerBalanceDto(AccountId, "acc-1", 0m, [new SubProfileBalanceDto("Alpha", null)])
            ]);

        var saved = await sut.SaveSnapshotAsync(request, CancellationToken.None);

        Assert.True(saved);
        account = await db.WorkerAccounts.SingleAsync();
        Assert.Equal(12500m, account.TotalBalance);
        Assert.Contains("12500", account.SubProfilesJson, StringComparison.Ordinal);

        var snapshot = await db.WorkerSnapshots.SingleAsync();
        var balances = JsonSerializer.Deserialize<List<WorkerBalanceDto>>(
            snapshot.BalancesJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        Assert.Single(balances);
        Assert.Equal(12500m, balances[0].TotalBalance);
    }

    [Fact]
    public async Task SaveSnapshotAsync_PreservesSubProfilesDisabledIdsJson()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: """["sp-2"]""");

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var capturedAt = DateTime.UtcNow;
        var request = new WorkerSnapshotRequest(
            WorkerId,
            capturedAt,
            CreateNonEmptyStats(),
            [
                new WorkerAccountDto(
                    AccountId,
                    "acc-1",
                    "Ok",
                    true,
                    1,
                    0,
                    0,
                    null,
                    capturedAt,
                    SubProfiles:
                    [
                        new WorkerSubProfileDto("sp-1", "Alpha", "", true, 100m, null, null, null),
                        new WorkerSubProfileDto("sp-2", "Beta", "", false, 200m, null, null, null)
                    ],
                    SubProfilesRefreshedAtUtc: capturedAt)
            ],
            [
                new WorkerBalanceDto(AccountId, "acc-1", 300m, [])
            ]);

        var saved = await sut.SaveSnapshotAsync(request, CancellationToken.None);

        Assert.True(saved);

        var account = await db.WorkerAccounts.SingleAsync();
        Assert.Equal(["sp-2"], SubProfilesDisabledIdsHelper.Parse(account.SubProfilesDisabledIdsJson));
        Assert.Contains("sp-1", account.SubProfilesJson);
        Assert.Contains("sp-2", account.SubProfilesJson);
    }

    [Fact]
    public async Task SaveSnapshotAsync_NullMultiloginIds_DoNotWipeExisting()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");
        var account = await db.WorkerAccounts.SingleAsync();
        account.AdsPowerProfileId = string.Empty;
        account.MultiloginProfileId = "mlx-profile";
        account.MultiloginFolderId = "mlx-folder";
        await db.SaveChangesAsync();

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var capturedAt = DateTime.UtcNow;
        var saved = await sut.SaveSnapshotAsync(
            new WorkerSnapshotRequest(
                WorkerId,
                capturedAt,
                CreateNonEmptyStats(),
                [
                    new WorkerAccountDto(
                        AccountId,
                        "acc-1",
                        "Ok",
                        true,
                        1,
                        0,
                        0,
                        null,
                        capturedAt,
                        SubProfiles: null)
                ],
                [
                    new WorkerBalanceDto(AccountId, "acc-1", 0m, [])
                ]),
            CancellationToken.None);

        Assert.True(saved);
        account = await db.WorkerAccounts.SingleAsync();
        Assert.Equal("mlx-profile", account.MultiloginProfileId);
        Assert.Equal("mlx-folder", account.MultiloginFolderId);
        Assert.Equal(string.Empty, account.AdsPowerProfileId);
    }

    [Fact]
    public async Task SaveSnapshotAsync_NullLocalUserDataDir_DoesNotWipeExisting()
    {
        await using var db = CreateDb();
        SeedWorkerWithAccount(db, disabledIdsJson: "[]");
        var account = await db.WorkerAccounts.SingleAsync();
        account.AdsPowerProfileId = string.Empty;
        account.LocalUserDataDir = @"D:\Orbita\ChromeProfiles\acc-1";
        await db.SaveChangesAsync();

        var sut = new TelemetryService(db, new OfficeAdminService(db), new NoopPanelRealtimeNotifier());
        var capturedAt = DateTime.UtcNow;
        var saved = await sut.SaveSnapshotAsync(
            new WorkerSnapshotRequest(
                WorkerId,
                capturedAt,
                CreateNonEmptyStats(),
                [
                    new WorkerAccountDto(
                        AccountId,
                        "acc-1",
                        "Ok",
                        true,
                        1,
                        0,
                        0,
                        null,
                        capturedAt,
                        SubProfiles: null)
                ],
                [
                    new WorkerBalanceDto(AccountId, "acc-1", 0m, [])
                ]),
            CancellationToken.None);

        Assert.True(saved);
        account = await db.WorkerAccounts.SingleAsync();
        Assert.Equal(@"D:\Orbita\ChromeProfiles\acc-1", account.LocalUserDataDir);
        Assert.Equal(string.Empty, account.AdsPowerProfileId);
    }

    private static DashboardStatsDto CreateNonEmptyStats() =>
        new(
            NewResponses: 0,
            TotalToday: 0,
            SentToCrm: 0,
            InProgress: 0,
            Duplicates: 0,
            Errors: 0,
            ActionRequired: 0,
            ConnectedAccounts: 1,
            RequiresAuthorization: 0,
            AccountsNeedAttentionCount: 0,
            ActiveAdsCount: 1,
            BlockedAdsCount: 0,
            DraftsCount: 0,
            HourlyActivity: [new ActivityPointDto("12:00", 1, 0, 0, 0, 12, 1, null)],
            WeeklyByDayActivity: []);

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedWorkerWithAccount(OrbitaDbContext db, string disabledIdsJson)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Test Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerId,
            OfficeId = OfficeId,
            DisplayName = "worker-1",
            MachineName = "pc",
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            MonitoringStatus = "Running",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = AccountId,
            AdsPowerProfileId = "profile-1",
            DisplayName = "acc-1",
            Status = "Ok",
            SubProfilesJson = JsonSerializer.Serialize(new[]
            {
                new WorkerSubProfileDto("sp-1", "Alpha", "", false, null, null, null, null),
                new WorkerSubProfileDto("sp-2", "Beta", "", false, null, null, null, null)
            }),
            SubProfilesDisabledIdsJson = disabledIdsJson,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}