using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BrowserMonitorServiceTests
{
    [Fact]
    public async Task StartAsync_SeedsBrowsersFromActiveAccounts()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var firstAccountId = Guid.NewGuid();
        var secondAccountId = Guid.NewGuid();

        SeedWorker(
            db,
            officeId,
            workerId,
            [
                new WorkerAccountEntity
                {
                    WorkerId = workerId,
                    AccountId = firstAccountId,
                    AdsPowerProfileId = "profile-1",
                    DisplayName = "Account 1",
                    UpdatedAtUtc = DateTime.UtcNow
                },
                new WorkerAccountEntity
                {
                    WorkerId = workerId,
                    AccountId = secondAccountId,
                    AdsPowerProfileId = "profile-2",
                    DisplayName = "Account 2",
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ],
            [
                new WorkerActiveAccountDto(firstAccountId, "Account 1", WorkerActivityPhases.Account, "Открыт браузер"),
                new WorkerActiveAccountDto(secondAccountId, "Account 2", WorkerActivityPhases.SubProfile, "Открыт субпрофиль", "sub-1", "Sub 1")
            ],
            ownerUserId: "op1");

        var pushNotifier = new CapturingWorkerPushNotifier();
        var service = CreateService(db, pushNotifier);

        var (session, error) = await service.StartAsync(
            workerId,
            TestPrincipalFactory.Operator("op1", "Operator 1", officeId));

        Assert.Null(error);
        Assert.NotNull(session);
        Assert.True(session!.WorkerNotified);
        Assert.Equal(2, session.Browsers.Count);

        var first = session.Browsers[0];
        Assert.Equal(firstAccountId, first.AccountId);
        Assert.Equal("profile-1", first.AdsPowerProfileId);
        Assert.Equal(1, first.Index);
        Assert.Equal(BrowserMonitorStatuses.Running, first.Status);
        Assert.Equal("Открыт браузер", first.StatusMessage);

        var second = session.Browsers[1];
        Assert.Equal(secondAccountId, second.AccountId);
        Assert.Equal("profile-2", second.AdsPowerProfileId);
        Assert.Equal(2, second.Index);
        Assert.Equal("sub-1", second.SubProfileId);
        Assert.Equal("Sub 1", second.SubProfileName);

        var pushedSession = Assert.Single(pushNotifier.BrowserMonitorSessions);
        Assert.Equal(session.Id, pushedSession.SessionId);
        Assert.Equal(2, pushedSession.Browsers.Count);
    }

    [Fact]
    public async Task GetPendingForWorkerAsync_DoesNotClearSeededBrowsers()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        SeedWorker(
            db,
            officeId,
            workerId,
            [
                new WorkerAccountEntity
                {
                    WorkerId = workerId,
                    AccountId = accountId,
                    AdsPowerProfileId = "profile-1",
                    DisplayName = "Account 1",
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ],
            [
                new WorkerActiveAccountDto(accountId, "Account 1", WorkerActivityPhases.Account, "Открыт браузер")
            ],
            ownerUserId: "op1");

        var service = CreateService(db);
        var (session, error) = await service.StartAsync(
            workerId,
            TestPrincipalFactory.Operator("op1", "Operator 1", officeId));

        Assert.Null(error);
        Assert.NotNull(session);
        Assert.Single(session!.Browsers);

        var pending = await service.GetPendingForWorkerAsync(workerId);

        Assert.NotNull(pending);
        var browser = Assert.Single(pending!.Browsers);
        Assert.Equal(accountId, browser.AccountId);
        Assert.Equal("profile-1", browser.AdsPowerProfileId);
        Assert.Equal("Открыт браузер", browser.StatusMessage);
    }

    private static BrowserMonitorService CreateService(
        OrbitaDbContext db,
        IWorkerPushNotifier? workerPushNotifier = null) =>
        new(
            db,
            new OfficeScopeService(db),
            workerPushNotifier ?? new NoopWorkerPushNotifier(),
            new BrowserMonitorRegistry());

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedWorker(
        OrbitaDbContext db,
        Guid officeId,
        Guid workerId,
        IReadOnlyList<WorkerAccountEntity> accounts,
        IReadOnlyList<WorkerActiveAccountDto> activeAccounts,
        string? ownerUserId = null)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Office",
            RegistrationSecretHash = "x",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            OwnerUserId = ownerUserId,
            DisplayName = "Worker 1",
            MachineName = "PC-1",
            ApiKeyHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            ActivityActiveAccountsJson = JsonSerializer.Serialize(activeAccounts)
        });
        db.WorkerAccounts.AddRange(accounts);
        db.SaveChanges();
    }

    private sealed class CapturingWorkerPushNotifier : IWorkerPushNotifier
    {
        public List<WorkerPendingBrowserMonitorSessionDto> BrowserMonitorSessions { get; } = [];

        public Task<bool> TryPushCommandAsync(Guid workerId, string command, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task PushConfigChangedAsync(Guid workerId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> TryPushCaptchaSessionAsync(
            Guid workerId,
            WorkerPendingCaptchaSessionDto session,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TryPushBrowserMonitorSessionAsync(
            Guid workerId,
            WorkerPendingBrowserMonitorSessionDto session,
            CancellationToken ct = default)
        {
            BrowserMonitorSessions.Add(session);
            return Task.FromResult(true);
        }

        public Task<bool> TryPushLocalChromeLoginSessionAsync(
            Guid workerId,
            WorkerPendingLocalChromeLoginDto session,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task DeliverPendingOnConnectAsync(Guid workerId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
