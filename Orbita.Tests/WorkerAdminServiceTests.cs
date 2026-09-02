using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class WorkerAdminServiceTests
{
    private static readonly Guid OfficeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OfficeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid WorkerA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task RenameAsync_OperatorFromWorkerOffice_ChangesName()
    {
        await using var db = CreateDb();
        await SeedAsync(db);

        var (worker, error) = await CreateService(db).RenameAsync(
            WorkerA, "Новое имя", OfficeScope.ForOffice(OfficeA));

        Assert.Null(error);
        Assert.Equal("Новое имя", worker!.DisplayName);
        Assert.Equal("Новое имя", (await db.Workers.SingleAsync(x => x.Id == WorkerA)).DisplayName);
    }

    [Fact]
    public async Task RenameAsync_OperatorFromAnotherOffice_ReturnsNotFoundAndKeepsName()
    {
        await using var db = CreateDb();
        await SeedAsync(db);

        var (worker, error) = await CreateService(db).RenameAsync(
            WorkerA, "Чужое имя", OfficeScope.ForOffice(OfficeB));

        Assert.Null(worker);
        Assert.Equal("Воркер не найден.", error);
        Assert.Equal("Воркер A", (await db.Workers.SingleAsync(x => x.Id == WorkerA)).DisplayName);
    }

    [Fact]
    public async Task RenameAsync_GlobalAdmin_ChangesNameInAnyOffice()
    {
        await using var db = CreateDb();
        await SeedAsync(db);

        var (worker, error) = await CreateService(db).RenameAsync(
            WorkerA, "Имя администратора", OfficeScope.GlobalAdmin);

        Assert.Null(error);
        Assert.Equal("Имя администратора", worker!.DisplayName);
    }

    [Fact]
    public async Task SetMonitoringPausedAsync_OperatorFromWorkerOffice_PausesWithoutDisabling()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var push = new CapturingPushNotifier();

        var (worker, error) = await CreateService(db, push).SetMonitoringPausedAsync(
            WorkerA, paused: true, OfficeScope.ForOffice(OfficeA));

        Assert.Null(error);
        Assert.True(worker!.IsMonitoringPaused);
        Assert.True(worker.IsEnabled);

        var stored = await db.Workers.SingleAsync(x => x.Id == WorkerA);
        Assert.True(stored.IsMonitoringPaused);
        Assert.True(stored.IsEnabled);
        Assert.Equal([WorkerA], push.ConfigChanged);
        Assert.Empty(push.Commands);
    }

    [Fact]
    public async Task SetMonitoringPausedAsync_OperatorFromAnotherOffice_ReturnsNotFound()
    {
        await using var db = CreateDb();
        await SeedAsync(db);

        var (worker, error) = await CreateService(db).SetMonitoringPausedAsync(
            WorkerA, paused: true, OfficeScope.ForOffice(OfficeB));

        Assert.Null(worker);
        Assert.Equal("Воркер не найден.", error);
        Assert.False((await db.Workers.SingleAsync(x => x.Id == WorkerA)).IsMonitoringPaused);
    }

    [Fact]
    public async Task SetMonitoringPausedAsync_Resume_ClearsFlagAndPushesConfig()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var stored = await db.Workers.SingleAsync(x => x.Id == WorkerA);
        stored.IsMonitoringPaused = true;
        await db.SaveChangesAsync();
        var push = new CapturingPushNotifier();

        var (worker, error) = await CreateService(db, push).SetMonitoringPausedAsync(
            WorkerA, paused: false, OfficeScope.ForOffice(OfficeA));

        Assert.Null(error);
        Assert.False(worker!.IsMonitoringPaused);
        Assert.True(worker.IsEnabled);
        Assert.False((await db.Workers.SingleAsync(x => x.Id == WorkerA)).IsMonitoringPaused);
        Assert.Equal([WorkerA], push.ConfigChanged);
        Assert.Empty(push.Commands);
    }

    private static WorkerAdminService CreateService(OrbitaDbContext db, CapturingPushNotifier? push = null) =>
        new(
            db,
            new ConfigurationBuilder().Build(),
            null!,
            new NoopPanelRealtimeNotifier(),
            new WorkerConnectionRegistry(),
            push ?? new CapturingPushNotifier(),
            new LeadExportQuotaService(db));

    private static async Task SeedAsync(OrbitaDbContext db)
    {
        db.Offices.AddRange(
            new OfficeEntity { Id = OfficeA, Name = "Офис A", RegistrationSecretHash = "hash", CreatedAtUtc = DateTime.UtcNow },
            new OfficeEntity { Id = OfficeB, Name = "Офис B", RegistrationSecretHash = "hash", CreatedAtUtc = DateTime.UtcNow });
        db.Workers.Add(new WorkerEntity
        {
            Id = WorkerA,
            OfficeId = OfficeA,
            DisplayName = "Воркер A",
            MachineName = "host-a",
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            CreatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static OrbitaDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class CapturingPushNotifier : IWorkerPushNotifier
    {
        public List<Guid> ConfigChanged { get; } = [];
        public List<(Guid WorkerId, string Command)> Commands { get; } = [];

        public Task<bool> TryPushCommandAsync(Guid workerId, string command, CancellationToken ct = default)
        {
            Commands.Add((workerId, command));
            return Task.FromResult(true);
        }

        public Task PushConfigChangedAsync(Guid workerId, CancellationToken ct = default)
        {
            ConfigChanged.Add(workerId);
            return Task.CompletedTask;
        }

        public Task<bool> TryPushCaptchaSessionAsync(
            Guid workerId,
            WorkerPendingCaptchaSessionDto session,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TryPushBrowserMonitorSessionAsync(
            Guid workerId,
            WorkerPendingBrowserMonitorSessionDto session,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TryPushLocalChromeLoginSessionAsync(
            Guid workerId,
            WorkerPendingLocalChromeLoginDto session,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task DeliverPendingOnConnectAsync(Guid workerId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
