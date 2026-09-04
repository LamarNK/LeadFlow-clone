using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class LocalChromeLoginSessionServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid LocalId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid AdsId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task StartAsync_LocalAccount_DoesNotEnableMonitoring()
    {
        await using var db = CreateDb();
        Seed(db);
        var push = new CapturingPush();
        var sut = CreateSut(db, push);
        var principal = Admin();

        var (session, error) = await sut.StartAsync(WorkerId, LocalId, principal);

        Assert.Null(error);
        Assert.NotNull(session);
        Assert.Equal(LocalId, session!.AccountId);
        Assert.DoesNotContain("LocalUserDataDir", JsonSerializer.Serialize(session), StringComparison.Ordinal);
        Assert.Single(push.Logins);
        var stored = await db.WorkerAccounts.SingleAsync(x => x.AccountId == LocalId);
        Assert.False(stored.IsEnabledInPanel);
        Assert.False(stored.IsEnabled);
        Assert.Equal("RequiresLogin", stored.Status);
    }

    [Fact]
    public async Task StartAsync_WhenAccountIsMonitoring_ReturnsError()
    {
        await using var db = CreateDb();
        Seed(db);
        var worker = await db.Workers.SingleAsync();
        worker.ActivityActiveAccountsJson = JsonSerializer.Serialize(new[]
        {
            new WorkerActiveAccountDto(LocalId, "local", WorkerActivityPhases.Account, "сбор")
        });
        await db.SaveChangesAsync();

        var (session, error) = await CreateSut(db, new CapturingPush()).StartAsync(WorkerId, LocalId, Admin());

        Assert.Null(session);
        Assert.Contains("мониторинга", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_RejectsAdsPower()
    {
        await using var db = CreateDb();
        Seed(db);
        var (session, error) = await CreateSut(db, new CapturingPush()).StartAsync(WorkerId, AdsId, Admin());
        Assert.Null(session);
        Assert.Contains("обычного браузера", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WhenAnotherLoginIsPending_ReturnsError()
    {
        await using var db = CreateDb();
        Seed(db);
        var sut = CreateSut(db, new CapturingPush());
        var first = await sut.StartAsync(WorkerId, LocalId, Admin());
        Assert.Null(first.Error);

        var extraId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = extraId,
            AdsPowerProfileId = string.Empty,
            LocalUserDataDir = LocalChromeProfileMarkers.CreateManaged(extraId),
            DisplayName = "local-2",
            Status = "RequiresLogin",
            IsEnabled = false,
            IsEnabledInPanel = false,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var (session, error) = await sut.StartAsync(WorkerId, extraId, Admin());
        Assert.Null(session);
        Assert.Contains("уже открытый браузер", error, StringComparison.Ordinal);
    }

    private static LocalChromeLoginSessionService CreateSut(OrbitaDbContext db, CapturingPush push)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<OfficeScopeService>();
        var provider = services.BuildServiceProvider();
        return new LocalChromeLoginSessionService(provider.GetRequiredService<IServiceScopeFactory>(), push);
    }

    private static ClaimsPrincipal Admin()
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.Role, PanelRoles.Admin));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "op"));
        identity.AddClaim(new Claim(ClaimTypes.Name, "Оператор"));
        return new ClaimsPrincipal(identity);
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void Seed(OrbitaDbContext db)
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
            MonitoringStatus = "Stopped",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = AdsId,
            AdsPowerProfileId = "profile-1",
            DisplayName = "ads",
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = WorkerId,
            AccountId = LocalId,
            AdsPowerProfileId = string.Empty,
            LocalUserDataDir = LocalChromeProfileMarkers.CreateManaged(LocalId),
            DisplayName = "local",
            Status = "RequiresLogin",
            IsEnabled = false,
            IsEnabledInPanel = false,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private sealed class CapturingPush : IWorkerPushNotifier
    {
        public List<WorkerPendingLocalChromeLoginDto> Logins { get; } = [];

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
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> TryPushLocalChromeLoginSessionAsync(
            Guid workerId,
            WorkerPendingLocalChromeLoginDto session,
            CancellationToken ct = default)
        {
            Logins.Add(session);
            return Task.FromResult(true);
        }

        public Task<bool> TryPushTopUpSessionAsync(
            Guid workerId,
            WorkerPendingTopUpSessionDto session,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task DeliverPendingOnConnectAsync(Guid workerId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
