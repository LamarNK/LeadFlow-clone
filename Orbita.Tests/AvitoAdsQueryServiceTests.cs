using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class AvitoAdsQueryServiceTests
{
    [Fact]
    public async Task GetListingsAsync_PagesRowsButKeepsSummaryForFullFilteredSet()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new OrbitaDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            DisplayName = "Worker",
            MachineName = "machine",
            ApiKeyHash = "hash",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.WorkerAccounts.Add(new WorkerAccountEntity
        {
            WorkerId = workerId,
            AccountId = accountId,
            DisplayName = "Account",
            Status = "Active",
            SubProfilesJson = """[{"id":"sp","name":"Sub","category":"","isCurrent":true,"balance":0}]""",
            UpdatedAtUtc = DateTime.UtcNow
        });

        var states = new[]
        {
            AvitoAdListingStates.Active,
            AvitoAdListingStates.ApproachingExpiry,
            AvitoAdListingStates.UnknownPublicationDate,
            AvitoAdListingStates.ParseFailed,
            AvitoAdListingStates.NotActive
        };
        for (var index = 0; index < states.Length; index++)
        {
            db.WorkerAvitoAds.Add(new WorkerAvitoAdEntity
            {
                Id = Guid.NewGuid(),
                WorkerId = workerId,
                AccountId = accountId,
                AvitoSubProfileId = "sp",
                AvitoItemId = (index + 1).ToString(),
                Title = $"Title {index + 1}",
                Url = $"https://example.test/{index + 1}",
                StatusText = "Активно",
                State = states[index],
                IsActive = states[index] != AvitoAdListingStates.NotActive,
                PublicationDateSource = AvitoAdPublicationDateSources.Exact,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(index + 1),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();

        var service = new AvitoAdsQueryService(db, new OfficeScopeService(db));
        var response = await service.GetListingsAsync(
            OfficeScope.GlobalAdmin,
            workerId: null,
            accountId: null,
            subProfileId: null,
            state: null,
            isActive: null,
            q: null,
            ct: CancellationToken.None,
            tab: "active",
            page: 2,
            pageSize: 2,
            sort: "title",
            sortDir: "asc");

        Assert.Equal(4, response.Total);
        Assert.Equal(2, response.Items.Count);
        Assert.Equal(4, response.Summary.ActiveCount);
        Assert.Equal(1, response.Summary.UnknownDateCount);
        Assert.Equal(1, response.Summary.ExpiringIn7DaysCount);
        Assert.Equal(0, response.Summary.ExpiresTodayCount);
        Assert.Equal(0, response.Summary.ExpiredCount);
    }
}
