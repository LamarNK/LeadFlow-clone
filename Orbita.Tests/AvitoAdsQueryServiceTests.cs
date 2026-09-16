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
        var scope = Assert.Single(response.ScopeSummaries);
        Assert.Equal(workerId, scope.WorkerId);
        Assert.Equal(accountId, scope.AccountId);
        Assert.Equal("sp", scope.AvitoSubProfileId);
        Assert.Equal(4, scope.ActiveCount);
        Assert.Equal(0, scope.UnpublishedCount);
        Assert.Equal(0, scope.ErrorCount);
    }

    [Fact]
    public async Task GetListingsAsync_ScopeSummariesRemainGlobalWhenAccountIsSelected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OrbitaDbContext>().UseSqlite(connection).Options;
        await using var db = new OrbitaDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var selectedAccountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity { Id = officeId, Name = "Office", RegistrationSecretHash = "hash", CreatedAtUtc = DateTime.UtcNow });
        db.Workers.Add(new WorkerEntity { Id = workerId, OfficeId = officeId, DisplayName = "Worker", MachineName = "machine", ApiKeyHash = "hash", CreatedAtUtc = DateTime.UtcNow });
        foreach (var accountId in new[] { selectedAccountId, otherAccountId })
        {
            db.WorkerAccounts.Add(new WorkerAccountEntity { WorkerId = workerId, AccountId = accountId, DisplayName = accountId == selectedAccountId ? "Selected" : "Other", Status = "Active", SubProfilesJson = "[]", UpdatedAtUtc = DateTime.UtcNow });
        }
        db.WorkerAvitoAds.AddRange(
            CreateAd(workerId, selectedAccountId, "selected-active", AvitoAdSourceTabs.Active, true),
            CreateAd(workerId, otherAccountId, "other-error", AvitoAdSourceTabs.Error, false),
            CreateAd(workerId, otherAccountId, "other-unpublished", AvitoAdSourceTabs.Unpublished, false));
        await db.SaveChangesAsync();

        var service = new AvitoAdsQueryService(db, new OfficeScopeService(db));
        var response = await service.GetListingsAsync(
            OfficeScope.GlobalAdmin,
            workerId: null,
            accountId: selectedAccountId,
            subProfileId: null,
            state: null,
            isActive: null,
            q: null,
            ct: CancellationToken.None,
            tab: "all");

        Assert.Single(response.Items);
        Assert.Equal(2, response.ScopeSummaries.Count);
        var other = Assert.Single(response.ScopeSummaries, x => x.AccountId == otherAccountId);
        Assert.Equal(0, other.ActiveCount);
        Assert.Equal(1, other.UnpublishedCount);
        Assert.Equal(1, other.ErrorCount);
    }

    private static WorkerAvitoAdEntity CreateAd(
        Guid workerId,
        Guid accountId,
        string itemId,
        string sourceTab,
        bool isActive) => new()
    {
        Id = Guid.NewGuid(),
        WorkerId = workerId,
        AccountId = accountId,
        AvitoSubProfileId = "sp",
        AvitoItemId = itemId,
        Title = itemId,
        Url = $"https://example.test/{itemId}",
        StatusText = isActive ? "Активно" : "Не опубликовано",
        SourceTab = sourceTab,
        State = isActive ? AvitoAdListingStates.Active : AvitoAdListingStates.NotActive,
        IsActive = isActive,
        PublicationDateSource = AvitoAdPublicationDateSources.Exact,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow
    };
}
