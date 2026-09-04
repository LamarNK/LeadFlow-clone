using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class TopUpSessionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly string ValidPngBase64 = Convert.ToBase64String(
        new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52
        });

    [Fact]
    public async Task CreateAsync_ComputesTargetAndAmount_AndPausesWorker()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);

        var (session, conflict) = await service.CreateAsync(workerId, accountId, principal);

        Assert.Null(conflict);
        Assert.NotNull(session);
        Assert.Equal(TopUpSessionStatuses.Requested, session!.Status);
        Assert.Equal(100m, session.CurrentBalance);
        Assert.Equal(300m, session.TargetBalance);
        Assert.Equal(200m, session.RequestedAmount);
        Assert.Equal(0, session.DailyResponseCount);

        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.True(worker.IsMonitoringPaused);
        Assert.Equal(session.Id, worker.TopUpPauseLeaseId);
    }

    [Fact]
    public async Task CreateAsync_WhenAlreadyPaused_PreservesPreviousState()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m, monitoringPaused: true);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);

        var (session, conflict) = await service.CreateAsync(workerId, accountId, principal);

        Assert.Null(conflict);
        Assert.NotNull(session);

        // Воркер уже был на паузе — сессия не приобретает аренду.
        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.True(worker.IsMonitoringPaused);
        Assert.Null(worker.TopUpPauseLeaseId);
    }

    [Fact]
    public async Task CreateAsync_SecondActiveSession_ReturnsConflict()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);

        var (first, firstConflict) = await service.CreateAsync(workerId, accountId, principal);
        Assert.Null(firstConflict);
        Assert.NotNull(first);

        var (_, secondConflict) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(secondConflict);
        Assert.Equal(first!.Id, secondConflict.ActiveSessionId);
    }

    [Fact]
    public async Task CreateAsync_BalanceAtOrAboveThreshold_ReturnsConflict()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 150m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);

        var (session, conflict) = await service.CreateAsync(workerId, accountId, principal);

        Assert.Null(session);
        Assert.NotNull(conflict);
    }

    [Fact]
    public async Task CreateAsync_CountsDailyResponses_FromCollectedAt()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        // 7 responses collected today (Moscow day) → target 900.
        var (start, _) = TopUpSessionRules.GetMoscowDayRange(Now.UtcDateTime);
        for (var i = 0; i < 7; i++)
        {
            db.CandidateResponses.Add(new CandidateResponseEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = workerId,
                AccountId = accountId,
                CollectedAt = start.AddMinutes(i),
                CreatedAt = start.AddMinutes(i),
                SourceResponseId = $"src-{i}"
            });
        }

        await db.SaveChangesAsync();

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);

        var (session, conflict) = await service.CreateAsync(workerId, accountId, principal);

        Assert.Null(conflict);
        Assert.NotNull(session);
        Assert.Equal(7, session!.DailyResponseCount);
        Assert.Equal(900m, session.TargetBalance);
        Assert.Equal(800m, session.RequestedAmount);
    }

    [Fact]
    public async Task CancelAsync_RestoresPause_WhenSessionPausedWorker()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);

        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        var (cancelled, error) = await service.CancelAsync(session!.Id, principal);
        Assert.True(cancelled, error);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Cancelled, stored.Status);
        Assert.NotNull(stored.CompletedAtUtc);

        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.False(worker.IsMonitoringPaused);
    }

    [Fact]
    public async Task CancelAsync_DoesNotUnpause_WhenWorkerWasAlreadyPaused()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m, monitoringPaused: true);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);

        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.CancelAsync(session!.Id, principal);

        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.True(worker.IsMonitoringPaused);
    }

    [Fact]
    public async Task UpdateStatusFromWorker_TransitionsThroughLifecycle()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        var (started, startError) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));
        Assert.True(started, startError);

        var (qr, qrError) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(
                session.Id,
                TopUpSessionStatuses.QrReady,
                QrImageBase64: ValidPngBase64,
                QrImageUrl: "https://www.avito.ru/qr"));
        Assert.True(qr, qrError);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.QrReady, stored.Status);
        Assert.NotNull(stored.StartedAtUtc);
        Assert.NotNull(stored.QrReadyAtUtc);
        Assert.Equal(ValidPngBase64, stored.QrImageBase64);
        Assert.Equal("https://www.avito.ru/qr", stored.QrImageUrl);
    }

    [Fact]
    public async Task UpdateStatusFromWorker_TerminalState_ReleasesPause()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        var (failed, error) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(
                session!.Id,
                TopUpSessionStatuses.Failed,
                FailureMessage: "QR expired"));
        Assert.True(failed, error);

        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.False(worker.IsMonitoringPaused);

        // Terminal: further updates rejected.
        var (again, againError) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session.Id, TopUpSessionStatuses.Started));
        Assert.False(again);
        Assert.Contains("завершена", againError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPendingForWorkerAsync_ReturnsActiveSession()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        var pending = await service.GetPendingForWorkerAsync(workerId);
        Assert.NotNull(pending);
        Assert.Equal(session!.Id, pending!.SessionId);
        Assert.Equal(accountId, pending.AccountId);
        Assert.Equal(300m, pending.TargetBalance);
        Assert.Equal(200m, pending.RequestedAmount);
    }

    [Fact]
    public async Task UpdateStatusFromWorker_BackwardsTransition_IsRejected()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        var (started, _) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));
        Assert.True(started);

        // Backwards: started → requested is not allowed.
        var (backwards, error) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session.Id, TopUpSessionStatuses.Requested));
        Assert.False(backwards);
        Assert.Contains("Недопустимый переход", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateStatusFromWorker_SkipsQrReady_IsRejected()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        // requested → qr_ready (skipping started) is not allowed.
        var (skipped, error) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.QrReady));
        Assert.False(skipped);
        Assert.Contains("Недопустимый переход", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateStatusFromWorker_AccountNoLongerOwned_IsRejected()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        // Remove the account from the worker.
        var account = await db.WorkerAccounts.FirstAsync(x => x.WorkerId == workerId && x.AccountId == accountId);
        db.WorkerAccounts.Remove(account);
        await db.SaveChangesAsync();

        var (ok, error) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));
        Assert.False(ok);
        Assert.Contains("не принадлежит воркеру", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateStatusFromWorker_TerminalTransition_ClearsQrData()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));
        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(
                session.Id,
                TopUpSessionStatuses.QrReady,
                QrImageBase64: ValidPngBase64,
                QrImageUrl: "https://www.avito.ru/qr"));

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(ValidPngBase64, stored.QrImageBase64);
        Assert.Equal("https://www.avito.ru/qr", stored.QrImageUrl);

        // Terminal transition clears QR data.
        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session.Id, TopUpSessionStatuses.Failed, FailureMessage: "x"));

        stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Null(stored.QrImageBase64);
        Assert.Null(stored.QrImageUrl);
    }

    [Fact]
    public async Task CancelAsync_ClearsQrData()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));
        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(
                session.Id,
                TopUpSessionStatuses.QrReady,
                QrImageBase64: ValidPngBase64,
                QrImageUrl: "https://www.avito.ru/qr"));

        await service.CancelAsync(session.Id, principal);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Cancelled, stored.Status);
        Assert.Null(stored.QrImageBase64);
        Assert.Null(stored.QrImageUrl);
    }

    [Fact]
    public async Task SweepExpiredAsync_ExpiresAndReleasesPause()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        // Force expiry by advancing time.
        var expiredService = CreateService(db, new DateTimeOffset(2026, 9, 3, 20, 0, 0, TimeSpan.Zero));
        var swept = await expiredService.SweepExpiredAsync();
        Assert.Equal(1, swept);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session!.Id);
        Assert.Equal(TopUpSessionStatuses.Expired, stored.Status);
        Assert.NotNull(stored.CompletedAtUtc);

        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.False(worker.IsMonitoringPaused);
    }

    [Fact]
    public async Task SweepExpiredAsync_DoesNotUnpauseManualPause()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        // Оператор вручную меняет паузу после старта сессии — это инвалидирует аренду.
        var worker = await db.Workers.FirstAsync(x => x.Id == workerId);
        worker.IsMonitoringPaused = true;
        worker.TopUpPauseLeaseId = null;
        worker.TopUpPauseLeaseVersion++;
        await db.SaveChangesAsync();

        var expiredService = CreateService(db, new DateTimeOffset(2026, 9, 3, 20, 0, 0, TimeSpan.Zero));
        await expiredService.SweepExpiredAsync();

        worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.True(worker.IsMonitoringPaused);
    }

    [Fact]
    public async Task PurgeQrDataAsync_RemovesStaleQrAfterRetention()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        // Move to QrReady then terminal (failed) — but terminal clears QR, so seed QR manually
        // on a completed session to exercise the retention purge path.
        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));
        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session.Id, TopUpSessionStatuses.Failed, FailureMessage: "x"));

        var stored = await db.TopUpSessions.FirstAsync(x => x.Id == session.Id);
        stored.QrImageBase64 = "stale-base64";
        stored.QrImageUrl = "https://stale.example";
        stored.CompletedAtUtc = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        // Advance time well past retention (6h).
        var purgeService = CreateService(db, new DateTimeOffset(2026, 9, 3, 20, 0, 0, TimeSpan.Zero));
        var purged = await purgeService.PurgeQrDataAsync();
        Assert.Equal(1, purged);

        stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Null(stored.QrImageBase64);
        Assert.Null(stored.QrImageUrl);
    }

    [Fact]
    public async Task CreateAsync_ExpiredActiveSession_IsReplaced()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (first, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(first);

        // Force the first session past expiry without sweeping.
        var stored = await db.TopUpSessions.FirstAsync(x => x.Id == first!.Id);
        stored.ExpiresAtUtc = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        // Create again with a later clock: should expire the old and create a new one.
        var laterService = CreateService(db, new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
        var (second, conflict) = await laterService.CreateAsync(workerId, accountId, principal);

        Assert.Null(conflict);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second!.Id);

        var old = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == first.Id);
        Assert.Equal(TopUpSessionStatuses.Expired, old.Status);
    }

    [Fact]
    public async Task CreateAsync_OfflineWorker_ReturnsConflict()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        // Воркер давно не выходил на связь и не подключён к хабу.
        var worker = await db.Workers.FirstAsync(x => x.Id == workerId);
        worker.LastSeenAtUtc = Now.UtcDateTime.AddMinutes(-30);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);

        var (session, conflict) = await service.CreateAsync(workerId, accountId, principal);

        Assert.Null(session);
        Assert.NotNull(conflict);
        Assert.Contains("оффлайн", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateStatusFromWorker_RejectsInvalidQrPayload()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));

        // Некорректный base64 (не PNG) должен быть отклонён.
        var (ok, error) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(
                session.Id,
                TopUpSessionStatuses.QrReady,
                QrImageBase64: "bm90LWEtcG5n", // "not-a-png"
                QrImageUrl: null));

        Assert.False(ok);
        Assert.NotNull(error);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Started, stored.Status);
        Assert.Null(stored.QrImageBase64);
    }

    [Fact]
    public async Task UpdateStatusFromWorker_RejectsArbitraryQrUrl()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));

        var (ok, error) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(
                session.Id,
                TopUpSessionStatuses.QrReady,
                QrImageBase64: null,
                QrImageUrl: "javascript:alert(1)"));

        Assert.False(ok);
        Assert.NotNull(error);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Started, stored.Status);
        Assert.Null(stored.QrImageUrl);
    }

    [Fact]
    public async Task CreateAsync_SameAccountDifferentWorker_ReturnsConflict()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerA = Guid.NewGuid();
        var workerB = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerA, accountId, balance: 100m);
        // Тот же аккаунт привязан ко второму воркеру (глобальная уникальность по AccountId).
        SeedWorker(db, officeId, workerB, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);

        var (first, firstConflict) = await service.CreateAsync(workerA, accountId, principal);
        Assert.Null(firstConflict);
        Assert.NotNull(first);

        // Второй воркер пытается создать сессию для того же аккаунта — конфликт.
        var (second, secondConflict) = await service.CreateAsync(workerB, accountId, principal);
        Assert.Null(second);
        Assert.NotNull(secondConflict);
        Assert.Equal(first!.Id, secondConflict.ActiveSessionId);
    }

    [Fact]
    public async Task CancelAsync_WithOtherActiveSession_KeepsWorkerPaused()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountA, balance: 100m);
        SeedWorkerAccount(db, workerId, accountB, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);

        var (sessionA, _) = await service.CreateAsync(workerId, accountA, principal);
        Assert.NotNull(sessionA);
        var (sessionB, _) = await service.CreateAsync(workerId, accountB, principal);
        Assert.NotNull(sessionB);

        // Обе сессии поставили паузу (первая — да, вторая — нет, т.к. уже на паузе).
        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.True(worker.IsMonitoringPaused);

        // Отменяем первую сессию: воркер должен остаться на паузе из-за второй активной сессии.
        var (cancelled, error) = await service.CancelAsync(sessionA!.Id, principal);
        Assert.True(cancelled, error);

        worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.True(worker.IsMonitoringPaused);

        // Отменяем вторую (последнюю) сессию: пауза снимается.
        var (cancelled2, error2) = await service.CancelAsync(sessionB!.Id, principal);
        Assert.True(cancelled2, error2);

        worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.False(worker.IsMonitoringPaused);
    }

    [Fact]
    public async Task UpdateStatusFromWorker_LateQrReady_DoesNotReviveCancelledSession()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));

        // Оператор отменяет сессию.
        var (cancelled, cancelError) = await service.CancelAsync(session.Id, principal);
        Assert.True(cancelled, cancelError);

        // Поздний QrReady от воркера не должен оживить отменённую сессию.
        var (ok, error) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(
                session.Id,
                TopUpSessionStatuses.QrReady,
                QrImageBase64: ValidPngBase64,
                QrImageUrl: null));

        Assert.False(ok);
        Assert.NotNull(error);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Cancelled, stored.Status);
        Assert.Null(stored.QrImageBase64);
    }

    [Fact]
    public async Task ClaimPaymentAsync_FromStarted_ClaimsAndSetsStatus()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));

        var result = await service.ClaimPaymentAsync(workerId, session.Id);

        Assert.True(result.Claimed, result.Error);
        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.PaymentClaimed, stored.Status);
        Assert.NotNull(stored.PaymentClaimedAtUtc);
    }

    [Fact]
    public async Task ClaimPaymentAsync_AfterCancel_Rejects()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));

        // Отмена выигрывает гонку.
        var (cancelled, cancelError) = await service.CancelAsync(session.Id, principal);
        Assert.True(cancelled, cancelError);

        var result = await service.ClaimPaymentAsync(workerId, session.Id);
        Assert.False(result.Claimed);
        Assert.NotNull(result.Error);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Cancelled, stored.Status);
    }

    [Fact]
    public async Task CancelAsync_AfterClaim_ReturnsConflict_AndDoesNotSetCancelled()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));

        // Claim выигрывает гонку.
        var claim = await service.ClaimPaymentAsync(workerId, session.Id);
        Assert.True(claim.Claimed, claim.Error);

        // Отмена после claim возвращает конфликт и НЕ помечает сессию отменённой.
        var (cancelled, cancelError) = await service.CancelAsync(session.Id, principal);
        Assert.False(cancelled);
        Assert.NotNull(cancelError);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.PaymentClaimed, stored.Status);
    }

    [Fact]
    public async Task ClaimPaymentAsync_FromRequested_Rejects()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        // requested → claim недопустим (нужен started).
        var result = await service.ClaimPaymentAsync(workerId, session!.Id);
        Assert.False(result.Claimed);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ManualUnpause_InvalidatesLease_AndIsNotReverted()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        // Оператор вручную снимает паузу — инвалидирует аренду.
        var worker = await db.Workers.FirstAsync(x => x.Id == workerId);
        worker.IsMonitoringPaused = false;
        worker.TopUpPauseLeaseId = null;
        worker.TopUpPauseLeaseVersion++;
        await db.SaveChangesAsync();

        // Завершение сессии не должно вернуть паузу.
        await service.CancelAsync(session!.Id, principal);

        worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.False(worker.IsMonitoringPaused);
    }

    [Fact]
    public async Task TwoTerminalSessions_DoNotLeavePauseOn()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountA, balance: 100m);
        SeedWorkerAccount(db, workerId, accountB, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);

        var (sessionA, _) = await service.CreateAsync(workerId, accountA, principal);
        Assert.NotNull(sessionA);
        var (sessionB, _) = await service.CreateAsync(workerId, accountB, principal);
        Assert.NotNull(sessionB);

        // Обе сессии завершаются (в любом порядке) — пауза должна сняться.
        await service.CancelAsync(sessionA!.Id, principal);
        await service.CancelAsync(sessionB!.Id, principal);

        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.False(worker.IsMonitoringPaused);
        Assert.Null(worker.TopUpPauseLeaseId);
    }

    [Fact]
    public async Task ClaimPaymentAsync_SessionAcquiredLease_ThenManualChange_Rejects()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));

        // Сессия приобрела аренду (воркер был не на паузе).
        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.True(stored.OwnsPauseLease);

        // Оператор вручную меняет паузу — инвалидирует аренду и инкрементирует версию.
        await SimulateManualPauseChangeAsync(db, workerId);

        var claim = await service.ClaimPaymentAsync(workerId, session.Id);
        Assert.False(claim.Claimed);
        Assert.NotNull(claim.Error);
    }

    [Fact]
    public async Task ClaimPaymentAsync_SessionCreatedWhileManuallyPaused_NoLaterChange_Allowed()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m, monitoringPaused: true);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));

        // Сессия создана при уже ручной паузе — аренду не приобретала.
        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.False(stored.OwnsPauseLease);

        // Без последующего ручного изменения — claim разрешён.
        var claim = await service.ClaimPaymentAsync(workerId, session.Id);
        Assert.True(claim.Claimed, claim.Error);
    }

    [Fact]
    public async Task ClaimPaymentAsync_SessionCreatedWhileManuallyPaused_ThenManualChange_Rejects()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m, monitoringPaused: true);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(session);

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));

        // Оператор вручную меняет паузу после создания сессии — версия инкрементируется.
        await SimulateManualPauseChangeAsync(db, workerId);

        var claim = await service.ClaimPaymentAsync(workerId, session.Id);
        Assert.False(claim.Claimed);
        Assert.NotNull(claim.Error);
    }

    private static async Task SimulateManualPauseChangeAsync(OrbitaDbContext db, Guid workerId)
    {
        var worker = await db.Workers.FirstAsync(x => x.Id == workerId);
        worker.IsMonitoringPaused = !worker.IsMonitoringPaused;
        worker.TopUpPauseLeaseId = null;
        worker.TopUpPauseLeaseVersion++;
        await db.SaveChangesAsync();
    }

    private static TopUpSessionService CreateService(
        OrbitaDbContext db,
        DateTimeOffset? now = null,
        WorkerConnectionRegistry? registry = null) =>
        new(
            db,
            new OfficeScopeService(db),
            new NoopPanelRealtimeNotifier(),
            new NoopWorkerPushNotifier(),
            registry ?? new WorkerConnectionRegistry(),
            new FixedTimeProvider(now ?? Now));

    private static void SeedWorker(
        OrbitaDbContext db,
        Guid officeId,
        Guid workerId,
        Guid accountId,
        decimal balance,
        bool monitoringPaused = false)
    {
        if (!db.Offices.Any(x => x.Id == officeId))
        {
            db.Offices.Add(new OfficeEntity { Id = officeId, Name = "Office", RegistrationSecretHash = "x", CreatedAtUtc = DateTime.UtcNow });
        }

        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            OwnerUserId = "op1",
            DisplayName = "W1",
            MachineName = "M1",
            ApiKeyHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = Now.UtcDateTime,
            IsMonitoringPaused = monitoringPaused
        });
        SeedWorkerAccount(db, workerId, accountId, balance);
        db.SaveChanges();
    }

    private static void SeedWorkerAccount(
        OrbitaDbContext db,
        Guid workerId,
        Guid accountId,
        decimal balance)
    {
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = workerId,
            AccountId = accountId,
            AdsPowerProfileId = "p1",
            DisplayName = "Acc1",
            TotalBalance = balance,
            UpdatedAtUtc = DateTime.UtcNow
        });
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new OrbitaDbContext(options);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
