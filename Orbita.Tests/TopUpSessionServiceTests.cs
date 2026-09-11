using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;
using System.Text.Json;

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
        Assert.False(string.IsNullOrWhiteSpace(session.ProgressMessage));
        Assert.Equal(100m, session.CurrentBalance);
        Assert.Equal(300m, session.TargetBalance);
        Assert.Equal(200m, session.RequestedAmount);
        Assert.Equal(0, session.DailyResponseCount);
        Assert.Equal(Now.UtcDateTime.Add(TopUpSessionRules.QueueTtl), session.ExpiresAtUtc);

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
    public async Task CreateAsync_ForSelectedSubProfile_UsesItsBalanceAndResponses()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(
            db,
            officeId,
            workerId,
            accountId,
            balance: 999m,
            subProfilesJson: """[{"Id":"target","Name":"Целевой","Balance":120},{"Id":"other","Name":"Другой","Balance":10}]""");

        var (start, _) = TopUpSessionRules.GetMoscowDayRange(Now.UtcDateTime);
        for (var i = 0; i < 6; i++)
        {
            db.CandidateResponses.Add(new CandidateResponseEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = workerId,
                AccountId = accountId,
                AvitoSubProfileId = "target",
                CollectedAt = start.AddMinutes(i),
                CreatedAt = start.AddMinutes(i),
                SourceResponseId = $"target-{i}"
            });
        }

        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = workerId,
            AccountId = accountId,
            AvitoSubProfileId = "other",
            CollectedAt = start,
            CreatedAt = start,
            SourceResponseId = "other-0"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, conflict) = await service.CreateAsync(workerId, accountId, principal, "target");

        Assert.Null(conflict);
        Assert.NotNull(session);
        Assert.Equal("target", session!.SubProfileId);
        Assert.Equal("Целевой", session.SubProfileName);
        Assert.Equal(120m, session.CurrentBalance);
        Assert.Equal(6, session.DailyResponseCount);
        Assert.Equal(900m, session.TargetBalance);
        Assert.Equal(780m, session.RequestedAmount);
    }

    [Fact]
    public async Task CreateAsync_RapidBalanceSpendRaisesTargetAboveDailyFallback()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);
        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = workerId,
            CapturedAtUtc = Now.UtcDateTime.AddMinutes(-20),
            BalancesJson = JsonSerializer.Serialize(new[]
            {
                new WorkerBalanceDto(accountId, "Acc1", 400m, [])
            })
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, conflict) = await service.CreateAsync(workerId, accountId, principal);

        Assert.Null(conflict);
        Assert.NotNull(session);
        Assert.Equal(0, session!.DailyResponseCount);
        Assert.Equal(900m, session.TargetBalance);
        Assert.Equal(800m, session.RequestedAmount);
    }

    [Fact]
    public async Task CreateAsync_BalanceHistoryOlderThanOneHourDoesNotRaiseTarget()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);
        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = workerId,
            CapturedAtUtc = Now.UtcDateTime.AddHours(-1).AddSeconds(-1),
            BalancesJson = JsonSerializer.Serialize(new[]
            {
                new WorkerBalanceDto(accountId, "Acc1", 2_000m, [])
            })
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, conflict) = await service.CreateAsync(workerId, accountId, principal);

        Assert.Null(conflict);
        Assert.NotNull(session);
        Assert.Equal(300m, session!.TargetBalance);
        Assert.Equal(200m, session.RequestedAmount);
    }

    [Fact]
    public async Task CreateAsync_RapidSpendUsesTheSelectedSubProfileOnly()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(
            db,
            officeId,
            workerId,
            accountId,
            balance: 2_100m,
            subProfilesJson: """[{"Id":"target","Name":"Целевой","Balance":100},{"Id":"other","Name":"Другой","Balance":2000}]""");
        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = workerId,
            CapturedAtUtc = Now.UtcDateTime.AddMinutes(-15),
            BalancesJson = JsonSerializer.Serialize(new[]
            {
                new WorkerBalanceDto(accountId, "Acc1", 2_500m,
                [
                    new SubProfileBalanceDto("Целевой", 400m, SubProfileId: "target"),
                    new SubProfileBalanceDto("Другой", 2_100m, SubProfileId: "other")
                ])
            })
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, conflict) = await service.CreateAsync(workerId, accountId, principal, "target");

        Assert.Null(conflict);
        Assert.NotNull(session);
        Assert.Equal(900m, session!.TargetBalance);
        Assert.Equal(800m, session.RequestedAmount);
    }

    [Fact]
    public async Task CreateAsync_RapidSpendCanUseAUniqueLegacySubProfileName()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(
            db,
            officeId,
            workerId,
            accountId,
            balance: 100m,
            subProfilesJson: """[{"Id":"target","Name":"Целевой","Balance":100}]""");
        db.WorkerSnapshots.Add(new WorkerSnapshotEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = workerId,
            CapturedAtUtc = Now.UtcDateTime.AddMinutes(-15),
            BalancesJson = JsonSerializer.Serialize(new[]
            {
                new WorkerBalanceDto(accountId, "Acc1", 400m,
                [new SubProfileBalanceDto("Целевой", 400m)])
            })
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, conflict) = await service.CreateAsync(workerId, accountId, principal, "target");

        Assert.Null(conflict);
        Assert.NotNull(session);
        Assert.Equal(900m, session!.TargetBalance);
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

        var startedAt = (await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id)).StartedAtUtc;
        var (progress, progressError) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(
                session.Id,
                TopUpSessionStatuses.Started,
                ProgressMessage: "Переключаем субпрофиль «Тест»…"));
        Assert.True(progress, progressError);

        var afterProgress = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Started, afterProgress.Status);
        Assert.Equal("Переключаем субпрофиль «Тест»…", afterProgress.ProgressMessage);
        Assert.Equal(startedAt, afterProgress.StartedAtUtc);

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
    public async Task GetOfficeAsync_History_ReturnsNewestFirst()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var firstAccount = Guid.NewGuid();
        var secondAccount = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, firstAccount, balance: 100m);
        SeedWorkerAccount(db, workerId, secondAccount, balance: 80m);
        await db.SaveChangesAsync();

        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (first, _) = await CreateService(db).CreateAsync(workerId, firstAccount, principal);
        var (second, _) = await CreateService(db, Now.AddMinutes(3)).CreateAsync(workerId, secondAccount, principal);
        Assert.NotNull(first);
        Assert.NotNull(second);

        var history = await CreateService(db, Now.AddMinutes(3)).GetOfficeAsync(principal, history: true);
        Assert.Equal(new[] { second!.Id, first!.Id }, history.Select(x => x.Id).ToArray());
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
    public async Task ClaimPaymentAsync_AfterProgressMessageUpdate_StillClaims()
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
            new UpdateTopUpSessionStatusRequest(
                session!.Id,
                TopUpSessionStatuses.Started,
                ProgressMessage: "Выбираем оплату через СБП…"));
        Assert.True(started, startError);

        var result = await service.ClaimPaymentAsync(workerId, session.Id);

        Assert.True(result.Claimed, result.Error);
        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.PaymentClaimed, stored.Status);
        Assert.Equal("Выбираем оплату через СБП…", stored.ProgressMessage);
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
    public async Task CancelAsync_AfterClaim_CancelsAndReleasesPause()
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

        var claim = await service.ClaimPaymentAsync(workerId, session.Id);
        Assert.True(claim.Claimed, claim.Error);

        var (cancelled, cancelError) = await service.CancelAsync(session.Id, principal);
        Assert.True(cancelled, cancelError);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Cancelled, stored.Status);

        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.False(worker.IsMonitoringPaused);
    }

    [Fact]
    public async Task MarkPaidAsync_FromQrReady_ReleasesPause()
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

        var (paid, error) = await service.MarkPaidAsync(session.Id, principal);
        Assert.True(paid, error);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.AwaitingBalance, stored.Status);
        Assert.Null(stored.CompletedAtUtc);
        Assert.NotNull(stored.AwaitingBalanceAtUtc);
        Assert.Null(stored.QrImageBase64);
        Assert.Null(stored.QrImageUrl);

        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.False(worker.IsMonitoringPaused);
        Assert.Null(worker.TopUpPauseLeaseId);
    }

    [Fact]
    public async Task MarkPaidAsync_BeforeQrReady_IsRejected()
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

        var (paid, error) = await service.MarkPaidAsync(session!.Id, principal);
        Assert.False(paid);
        Assert.NotNull(error);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Requested, stored.Status);

        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.True(worker.IsMonitoringPaused);
    }

    [Fact]
    public async Task MarkPaidAsync_IsIdempotent_WhenAlreadyAwaitingBalance()
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
                QrImageBase64: ValidPngBase64));

        Assert.True((await service.MarkPaidAsync(session.Id, principal)).Success);
        var (again, error) = await service.MarkPaidAsync(session.Id, principal);
        Assert.True(again, error);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.AwaitingBalance, stored.Status);
    }

    [Fact]
    public async Task UpdateStatusFromWorker_Paid_IsRejected()
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
                QrImageBase64: ValidPngBase64));

        var (ok, error) = await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(session.Id, TopUpSessionStatuses.Paid));

        Assert.False(ok);
        Assert.NotNull(error);

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.QrReady, stored.Status);
        Assert.False((await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId)).IsMonitoringPaused);
    }

    [Fact]
    public async Task CreateAsync_AfterPaymentClaim_WaitsForBalanceConfirmation()
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

        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(first!.Id, TopUpSessionStatuses.Started));
        await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(
                first.Id,
                TopUpSessionStatuses.QrReady,
                QrImageBase64: ValidPngBase64));
        Assert.True((await service.MarkPaidAsync(first.Id, principal)).Success);

        var (second, conflict) = await service.CreateAsync(workerId, accountId, principal);
        Assert.NotNull(conflict);
        Assert.Null(second);
        Assert.False((await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId)).IsMonitoringPaused);
    }

    [Fact]
    public async Task ConfirmBalancesAsync_CompletesAwaitingSession()
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
        await service.UpdateStatusFromWorkerAsync(workerId,
            new UpdateTopUpSessionStatusRequest(session!.Id, TopUpSessionStatuses.Started));
        await service.UpdateStatusFromWorkerAsync(workerId,
            new UpdateTopUpSessionStatusRequest(session.Id, TopUpSessionStatuses.QrReady, QrImageBase64: ValidPngBase64));
        Assert.True((await service.MarkPaidAsync(session.Id, principal)).Success);

        var capturedAt = Now.AddMinutes(2).UtcDateTime;
        var completed = await service.ConfirmBalancesAsync(
            workerId,
            [new WorkerBalanceDto(accountId, "Acc1", 300m, [])],
            capturedAt);

        Assert.Equal(1, completed);
        var stored = await db.TopUpSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Completed, stored.Status);
        Assert.Equal(300m, stored.BalanceAfter);
        Assert.Equal(capturedAt, stored.BalanceConfirmedAtUtc);
    }

    [Fact]
    public async Task SweepExpiredAsync_KeepsAwaitingBalanceUntilConfirmationTtl()
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
        await AdvanceToQrReadyAsync(service, workerId, session!.Id);
        Assert.True((await service.MarkPaidAsync(session.Id, principal)).Success);

        var stillWaiting = CreateService(db, Now.AddHours(23));
        Assert.Equal(0, await stillWaiting.SweepExpiredAsync());
        Assert.Equal(
            TopUpSessionStatuses.AwaitingBalance,
            (await db.TopUpSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);
    }

    [Fact]
    public async Task SweepExpiredAsync_FailsUnconfirmedPaymentAfter24Hours()
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
        await AdvanceToQrReadyAsync(service, workerId, session!.Id);
        Assert.True((await service.MarkPaidAsync(session.Id, principal)).Success);

        var sweeper = CreateService(db, Now.Add(TopUpSessionRules.BalanceConfirmationTtl));
        await sweeper.SweepExpiredAsync();

        var stored = await db.TopUpSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Failed, stored.Status);
        Assert.Contains("24 часа", stored.FailureMessage, StringComparison.Ordinal);
        Assert.NotNull(stored.CompletedAtUtc);
    }

    [Fact]
    public async Task SweepExpiredAsync_DoesNotExpireRequestedBeforeQueueTtl()
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

        var notYet = CreateService(db, Now.Add(TopUpSessionRules.PauseLeaseTtl));
        Assert.Equal(0, await notYet.SweepExpiredAsync());

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session!.Id);
        Assert.Equal(TopUpSessionStatuses.Requested, stored.Status);
        Assert.True((await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId)).IsMonitoringPaused);
    }

    [Fact]
    public async Task SweepExpiredAsync_ExpiresRequestedAtQueueTtl()
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

        var expiredService = CreateService(db, Now.Add(TopUpSessionRules.QueueTtl));
        Assert.Equal(1, await expiredService.SweepExpiredAsync());

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session!.Id);
        Assert.Equal(TopUpSessionStatuses.Expired, stored.Status);
        Assert.False((await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId)).IsMonitoringPaused);
    }

    [Fact]
    public async Task SweepExpiredAsync_DoesNotExpireBeforePauseLeaseTtl()
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

        var notYet = CreateService(db, Now.Add(TopUpSessionRules.PauseLeaseTtl).AddSeconds(-1));
        Assert.Equal(0, await notYet.SweepExpiredAsync());

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Started, stored.Status);
        Assert.True((await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId)).IsMonitoringPaused);
    }

    [Fact]
    public async Task SweepExpiredAsync_ExpiresAtPauseLeaseTtl()
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

        var expiredService = CreateService(db, Now.Add(TopUpSessionRules.PauseLeaseTtl));
        Assert.Equal(1, await expiredService.SweepExpiredAsync());

        var stored = await db.TopUpSessions.AsNoTracking().FirstAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Expired, stored.Status);
        Assert.False((await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId)).IsMonitoringPaused);
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

    [Fact]
    public async Task GetPendingForWorkerAsync_ReturnsOldestDispatchableSession()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var firstAccount = Guid.NewGuid();
        var secondAccount = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, firstAccount, balance: 100m);
        SeedWorkerAccount(db, workerId, secondAccount, balance: 80m);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (first, _) = await service.CreateAsync(workerId, firstAccount, principal);
        var (second, _) = await service.CreateAsync(workerId, secondAccount, principal);
        Assert.NotNull(first);
        Assert.NotNull(second);

        var pending = await service.GetPendingForWorkerAsync(workerId);
        Assert.Equal(first!.Id, pending!.SessionId);

        await AdvanceToQrReadyAsync(service, workerId, first.Id);

        pending = await service.GetPendingForWorkerAsync(workerId);
        Assert.Equal(second!.Id, pending!.SessionId);
        Assert.Equal(
            TopUpSessionStatuses.QrReady,
            (await db.TopUpSessions.AsNoTracking().SingleAsync(x => x.Id == first.Id)).Status);
    }

    [Fact]
    public async Task GetPendingForWorkerAsync_SkipsQrReadySession()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        await AdvanceToQrReadyAsync(service, workerId, session!.Id);

        Assert.Null(await service.GetPendingForWorkerAsync(workerId));
    }

    [Fact]
    public async Task QrReady_ReleasesPause_WhenNoOtherAutomation()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        await AdvanceToQrReadyAsync(service, workerId, session!.Id);

        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.False(worker.IsMonitoringPaused);
        Assert.Null(worker.TopUpPauseLeaseId);
        Assert.Equal(TopUpSessionStatuses.QrReady, (await db.TopUpSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);
    }

    [Fact]
    public async Task QrReady_KeepsPause_WhenAnotherSessionIsQueued()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var firstAccount = Guid.NewGuid();
        var secondAccount = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, firstAccount, balance: 100m);
        SeedWorkerAccount(db, workerId, secondAccount, balance: 80m);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (first, _) = await service.CreateAsync(workerId, firstAccount, principal);
        var (second, _) = await service.CreateAsync(workerId, secondAccount, principal);
        Assert.NotNull(second);
        await AdvanceToQrReadyAsync(service, workerId, first!.Id);

        var worker = await db.Workers.AsNoTracking().FirstAsync(x => x.Id == workerId);
        Assert.True(worker.IsMonitoringPaused);
        Assert.Equal(TopUpSessionStatuses.Requested, (await db.TopUpSessions.AsNoTracking().SingleAsync(x => x.Id == second!.Id)).Status);
    }

    [Fact]
    public async Task ConfirmBalancesAsync_DoesNotCompleteSmallIncrease()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        await AdvanceToQrReadyAsync(service, workerId, session!.Id);
        Assert.True((await service.MarkPaidAsync(session.Id, principal)).Success);

        var completed = await service.ConfirmBalancesAsync(
            workerId,
            [new WorkerBalanceDto(accountId, "Acc1", 101m, [])],
            Now.AddMinutes(2).UtcDateTime);

        Assert.Equal(0, completed);
        Assert.Equal(TopUpSessionStatuses.AwaitingBalance, (await db.TopUpSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);
    }

    [Fact]
    public async Task ConfirmBalancesAsync_MatchesSubProfileByIdEvenIfNameChanged()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(
            db,
            officeId,
            workerId,
            accountId,
            balance: 100m,
            subProfilesJson: """[{"Id":"target","Name":"Целевой","Balance":100}]""");

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal, "target");
        await AdvanceToQrReadyAsync(service, workerId, session!.Id);
        Assert.True((await service.MarkPaidAsync(session.Id, principal)).Success);

        var completed = await service.ConfirmBalancesAsync(
            workerId,
            [new WorkerBalanceDto(accountId, "Acc1", 300m,
            [
                new SubProfileBalanceDto("Переименованный", 300m, SubProfileId: "target")
            ])],
            Now.AddMinutes(2).UtcDateTime);

        Assert.Equal(1, completed);
        var stored = await db.TopUpSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Completed, stored.Status);
        Assert.Equal(300m, stored.BalanceAfter);
    }

    [Fact]
    public async Task ConfirmBalancesAsync_CompletesQrReadyWhenAmountMatches()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        await AdvanceToQrReadyAsync(service, workerId, session!.Id);

        var completed = await service.ConfirmBalancesAsync(
            workerId,
            [new WorkerBalanceDto(accountId, "Acc1", 300m, [])],
            Now.AddMinutes(2).UtcDateTime);

        Assert.Equal(1, completed);
        var stored = await db.TopUpSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
        Assert.Equal(TopUpSessionStatuses.Completed, stored.Status);
        Assert.Null(stored.QrImageBase64);
    }

    [Fact]
    public async Task ConfirmBalancesAsync_CompletesAwaitingBalanceOnLaterPass()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        await AdvanceToQrReadyAsync(service, workerId, session!.Id);
        Assert.True((await service.MarkPaidAsync(session.Id, principal)).Success);

        var later = CreateService(db, Now.AddHours(6));
        Assert.Equal(0, await later.SweepExpiredAsync());

        var completed = await later.ConfirmBalancesAsync(
            workerId,
            [new WorkerBalanceDto(accountId, "Acc1", 300m, [])],
            Now.AddHours(6).UtcDateTime);

        Assert.Equal(1, completed);
        Assert.Equal(TopUpSessionStatuses.Completed, (await db.TopUpSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);
    }

    [Fact]
    public async Task MarkPaidAsync_IsIdempotent_WhenAlreadyCompleted()
    {
        await using var db = CreateDb();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        SeedWorker(db, officeId, workerId, accountId, balance: 100m);

        var service = CreateService(db);
        var principal = TestPrincipalFactory.Operator("op1", "Operator 1", officeId);
        var (session, _) = await service.CreateAsync(workerId, accountId, principal);
        await AdvanceToQrReadyAsync(service, workerId, session!.Id);
        await service.ConfirmBalancesAsync(
            workerId,
            [new WorkerBalanceDto(accountId, "Acc1", 300m, [])],
            Now.AddMinutes(2).UtcDateTime);

        var (paid, error) = await service.MarkPaidAsync(session.Id, principal);
        Assert.True(paid, error);
        Assert.Equal(TopUpSessionStatuses.Completed, (await db.TopUpSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id)).Status);
    }

    private static async Task AdvanceToQrReadyAsync(TopUpSessionService service, Guid workerId, Guid sessionId)
    {
        Assert.True((await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(sessionId, TopUpSessionStatuses.Started))).Success);
        Assert.True((await service.UpdateStatusFromWorkerAsync(
            workerId,
            new UpdateTopUpSessionStatusRequest(sessionId, TopUpSessionStatuses.QrReady, QrImageBase64: ValidPngBase64))).Success);
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
        bool monitoringPaused = false,
        string subProfilesJson = "[]")
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
        SeedWorkerAccount(db, workerId, accountId, balance, subProfilesJson);
        db.SaveChanges();
    }

    private static void SeedWorkerAccount(
        OrbitaDbContext db,
        Guid workerId,
        Guid accountId,
        decimal balance,
        string subProfilesJson = "[]")
    {
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = workerId,
            AccountId = accountId,
            AdsPowerProfileId = "p1",
            DisplayName = "Acc1",
            TotalBalance = balance,
            SubProfilesJson = subProfilesJson,
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
