using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;
using Orbita.Tests;

namespace Orbita.Tests;

public sealed class CaptchaSessionServiceTests
{
    [Fact]
    public async Task CreateAsync_SecondSessionOnSameWorker_ReturnsConflict()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity { Id = officeId, Name = "Office", RegistrationSecretHash = "x", CreatedAtUtc = DateTime.UtcNow });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            OwnerUserId = "op1",
            DisplayName = "W1",
            MachineName = "M1",
            ApiKeyHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = workerId,
            AccountId = accountId,
            AdsPowerProfileId = "p1",
            DisplayName = "Acc1",
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var request = new CreateCaptchaSessionRequest(accountId, workerId, "https://www.avito.ru/captcha", "geetest");

        var (first, firstConflict) = await service.CreateAsync(request, principal);
        Assert.Null(firstConflict);
        Assert.NotNull(first);

        // Admin can access any worker; second session on same worker must conflict.
        var admin = TestPrincipalFactory.Admin("admin1", "Admin");
        var (_, secondConflict) = await service.CreateAsync(request, admin);
        Assert.NotNull(secondConflict);
        Assert.Contains("занят", secondConflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelAsync_PreventsWorkerFromReopeningCancelledSession_AndBroadcastsState()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, ownerUserId: "op1");

        var relay = new CapturingCaptchaSessionRelayNotifier();
        var service = CreateService(db, relay);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var request = new CreateCaptchaSessionRequest(accountId, workerId, "https://www.avito.ru/captcha", "geetest");

        var (session, conflict) = await service.CreateAsync(request, principal);
        Assert.Null(conflict);
        Assert.NotNull(session);

        var (cancelled, cancelError) = await service.CancelAsync(session!.Id, principal);
        Assert.True(cancelled, cancelError);

        var (updated, updateError) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateCaptchaSessionStatusRequest(session.Id, CaptchaSessionStatuses.Active));

        Assert.False(updated);
        Assert.Contains("завершена", updateError, StringComparison.OrdinalIgnoreCase);

        var stored = await db.CaptchaSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(CaptchaSessionStatuses.Cancelled, stored.Status);
        Assert.Contains(
            relay.Messages,
            x => x.SessionId == session.Id
                && x.Status == CaptchaSessionStatuses.Cancelled);
    }

    [Fact]
    public async Task SweepExpiredAsync_ExpiresActiveSession_AndReleasesWorkerLock()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, ownerUserId: "op1");
        var sessionId = Guid.NewGuid();
        var worker = db.Workers.Single(x => x.Id == workerId);
        worker.ActiveCaptchaSessionId = sessionId;
        worker.ActiveCaptchaSessionStartedAtUtc = DateTime.UtcNow.AddMinutes(-10);
        db.CaptchaSessions.Add(new CaptchaSessionEntity
        {
            Id = sessionId,
            WorkerId = workerId,
            AccountId = accountId,
            AccountName = "Acc1",
            OfficeId = officeId,
            OperatorUserId = "op1",
            OperatorDisplayName = "Operator 1",
            PageUrl = "https://www.avito.ru/captcha",
            CaptchaKind = "geetest",
            Status = CaptchaSessionStatuses.Active,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-15),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        db.CaptchaSessions.Add(new CaptchaSessionEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = workerId,
            AccountId = accountId,
            AccountName = "Acc1",
            OfficeId = officeId,
            OperatorUserId = "op1",
            OperatorDisplayName = "Operator 1",
            PageUrl = "https://www.avito.ru/captcha",
            CaptchaKind = "geetest",
            Status = CaptchaSessionStatuses.Cancelled,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-15),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var swept = await service.SweepExpiredAsync();

        Assert.Equal(1, swept);
        var expired = await db.CaptchaSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId);
        Assert.Equal(CaptchaSessionStatuses.Expired, expired.Status);
        Assert.Equal("Истекло время сессии.", expired.FailureMessage);
        Assert.NotNull(expired.CompletedAtUtc);
        var released = await db.Workers.AsNoTracking().SingleAsync(x => x.Id == workerId);
        Assert.Null(released.ActiveCaptchaSessionId);
    }

    private static CaptchaSessionService CreateService(
        OrbitaDbContext db,
        ICaptchaSessionRelayNotifier? relayNotifier = null)
    {
        var officeScope = new OfficeScopeService(db);
        return new CaptchaSessionService(
            db,
            officeScope,
            new NoopCaptchaLockNotifier(),
            new NoopPanelRealtimeNotifier(),
            new NoopWorkerPushNotifier(),
            relayNotifier ?? new NoopCaptchaSessionRelayNotifier());
    }

    private static void SeedWorker(
        OrbitaDbContext db,
        Guid officeId,
        Guid workerId,
        Guid accountId,
        string? ownerUserId = null)
    {
        db.Offices.Add(new OfficeEntity { Id = officeId, Name = "Office", RegistrationSecretHash = "x", CreatedAtUtc = DateTime.UtcNow });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            OwnerUserId = ownerUserId,
            DisplayName = "W1",
            MachineName = "M1",
            ApiKeyHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = workerId,
            AccountId = accountId,
            AdsPowerProfileId = "p1",
            DisplayName = "Acc1",
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new OrbitaDbContext(options);
    }

    private sealed class NoopCaptchaLockNotifier : ICaptchaLockNotifier
    {
        public Task NotifyLockChangedAsync(Guid workerId, Guid officeId, WorkerCaptchaLockDto lockState, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class CapturingCaptchaSessionRelayNotifier : ICaptchaSessionRelayNotifier
    {
        public List<CaptchaStateChangedMessage> Messages { get; } = [];

        public Task NotifyStateChangedAsync(CaptchaStateChangedMessage message, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
