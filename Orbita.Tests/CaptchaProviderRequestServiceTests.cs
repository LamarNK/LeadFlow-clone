using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CaptchaProviderRequestServiceTests
{
    [Fact]
    public void ToUtcBoundary_AlwaysReturnsUtcKind()
    {
        var local = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Unspecified);

        var result = CaptchaProviderRequestService.ToUtcBoundary(local, -300, DateTime.MinValue);
        var fallback = CaptchaProviderRequestService.ToUtcBoundary(null, -300, DateTime.MaxValue);

        Assert.Equal(new DateTime(2026, 9, 14, 5, 0, 0, DateTimeKind.Utc), result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(DateTimeKind.Utc, fallback.Kind);
        Assert.Equal(DateTime.MaxValue, fallback);
    }

    [Fact]
    public async Task Lifecycle_PersistsSafeContextAndTargetDiagnostics()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrbitaDbContext>().UseSqlite(connection).Options;
        await using var db = new OrbitaDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity { Id = officeId, Name = "Office", RegistrationSecretHash = "hash", CreatedAtUtc = DateTime.UtcNow });
        db.Workers.Add(new WorkerEntity { Id = workerId, OfficeId = officeId, DisplayName = "Worker", MachineName = "machine", ApiKeyHash = "hash", CreatedAtUtc = DateTime.UtcNow });
        db.WorkerAccounts.Add(new WorkerAccountEntity { WorkerId = workerId, AccountId = accountId, DisplayName = "Account", Status = "Active", UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var service = new CaptchaProviderRequestService(db, new OfficeScopeService(db));
        var id = Guid.NewGuid();
        var created = await service.CreateAsync(workerId, new CaptchaProviderRequestCreateDto(
            id, accountId, null, null, null, null, "rucaptcha", "geetest_v4", "other", "firewall_detected",
            1, 3, "https://www.avito.ru/", DateTime.UtcNow, null,
            new CaptchaContextDiagnosticsDto("response", new string('a', 64), true, true, 125)), CancellationToken.None);

        Assert.Equal(id, created);
        Assert.True(await service.MarkProviderAcceptedAsync(workerId,
            new CaptchaProviderRequestProviderResultDto(id, "accepted", "task-1", null, DateTime.UtcNow, 15300), CancellationToken.None));
        Assert.True(await service.MarkTargetOutcomeAsync(workerId,
            new CaptchaProviderRequestTargetResultDto(id, "rejected", DateTime.UtcNow, "verified_false", 200, 15500), CancellationToken.None));

        var row = await db.CaptchaProviderRequests.AsNoTracking().SingleAsync();
        Assert.Equal(new string('a', 64), row.ContextFingerprint);
        Assert.True(row.ChallengePresent);
        Assert.True(row.RiskTypePresent);
        Assert.Equal(15300, row.SolveDurationMs);
        Assert.Equal("verified_false", row.TargetReason);
        Assert.Equal(200, row.TargetHttpStatus);
        Assert.Equal(15500, row.ContextAgeAtVerifyMs);

        var statistics = await service.GetStatisticsAsync(
            OfficeScope.GlobalAdmin, null, null, null, null, 0, CancellationToken.None);
        Assert.Equal(1, statistics.ContextDetectedCount);
        Assert.Equal(1, statistics.ChallengePresentCount);
        Assert.Equal(1, statistics.RiskTypePresentCount);
        Assert.Equal("verified_false", Assert.Single(statistics.Workers).Requests.Single().TargetReason);
    }
}
