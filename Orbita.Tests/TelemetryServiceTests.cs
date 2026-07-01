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