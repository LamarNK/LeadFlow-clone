using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CandidatePhoneWatchServiceTests
{
    [Fact]
    public async Task UpsertAsync_PersistsWatchChatAndReturnsItFromLookup()
    {
        await using var db = CreateDb();
        var (worker, person, response) = Seed(db);
        var accountId = Guid.NewGuid();
        var candidate = new WorkerCandidateDto(
            accountId,
            "Avito",
            "Avito",
            "avito:real-response",
            "card-fingerprint",
            person.FullName,
            person.Age,
            null,
            "+79001111111",
            person.City,
            "Охранник",
            "https://www.avito.ru/1",
            "https://www.avito.ru/messenger",
            "sub-1",
            "",
            "[{\"text\":\"Здравствуйте\"}]",
            DateTime.UtcNow);

        await new CandidatePhoneWatchService(db).UpsertAsync(
            worker,
            candidate,
            person.Id,
            response.Id,
            "79001111111",
            WorkerCandidateOperationKinds.NewResponse,
            CancellationToken.None);

        var stored = await db.CandidatePhoneWatches.SingleAsync();
        Assert.Equal(response.Id, stored.CanonicalResponseId);
        Assert.Equal("[{\"text\":\"Здравствуйте\"}]", stored.ChatMessagesJson);
        Assert.NotEmpty(stored.ChatFingerprint);
        Assert.NotEmpty(stored.ProfileFingerprint);
        Assert.StartsWith("phone-watch:", stored.PublishedSourceResponseId);

        var lookup = new CandidateLookupService(
            db,
            new CandidatePersonMatchService(db),
            new CandidatePhoneWatchService(db));
        var result = await lookup.LookupAsync(
            worker.Id,
            new WorkerCandidateLookupRequest(
                accountId,
                "PerAvitoAccount",
                [stored.PublishedSourceResponseId],
                [],
                IncludeSourceResponseMetadata: true,
                AvitoSubProfileId: "sub-1",
                OpenPhoneWatchHours: 120));

        Assert.NotNull(result);
        Assert.Single(result!.ExistingSourceResponses!);
        Assert.Single(result.OpenPhoneWatches!);
    }

    [Fact]
    public async Task UpsertAsync_UnchangedRefreshUpdatesSameWatch()
    {
        await using var db = CreateDb();
        var (worker, person, response) = Seed(db);
        var accountId = Guid.NewGuid();
        var first = Candidate(accountId, person, DateTime.UtcNow.AddMinutes(-5));
        var service = new CandidatePhoneWatchService(db);

        await service.UpsertAsync(
            worker,
            first,
            person.Id,
            response.Id,
            "79001111111",
            WorkerCandidateOperationKinds.NewResponse,
            CancellationToken.None);
        var firstSeen = (await db.CandidatePhoneWatches.SingleAsync()).LastSeenUtc;

        var refresh = Candidate(accountId, person, DateTime.UtcNow);
        await service.UpsertAsync(
            worker,
            refresh,
            person.Id,
            response.Id,
            "79001111111",
            WorkerCandidateOperationKinds.WatchRefresh,
            CancellationToken.None);

        Assert.Single(await db.CandidatePhoneWatches.ToListAsync());
        Assert.True((await db.CandidatePhoneWatches.SingleAsync()).LastSeenUtc > firstSeen);
    }

    [Fact]
    public async Task CloseForCardsAsync_StopsActiveWatchBeforeWindowExpires()
    {
        await using var db = CreateDb();
        var (worker, person, response) = Seed(db);
        var card = new CrmCandidateCardEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = response.Id,
            OfficeId = worker.OfficeId,
            Stage = CrmStages.Lead,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            StageChangedAtUtc = DateTime.UtcNow
        };
        db.Add(card);
        db.SaveChanges();

        var accountId = Guid.NewGuid();
        var service = new CandidatePhoneWatchService(db);
        await service.UpsertAsync(
            worker,
            Candidate(accountId, person, DateTime.UtcNow.AddHours(-1)),
            person.Id,
            response.Id,
            "79001111111",
            WorkerCandidateOperationKinds.NewResponse,
            CancellationToken.None);

        var closedCount = await service.CloseForCardsAsync([card], DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(1, closedCount);
        var watch = await db.CandidatePhoneWatches.SingleAsync();
        Assert.Equal(CandidatePhoneWatchStates.ClosedInCrm, watch.State);
    }

    [Fact]
    public async Task UpsertAsync_DoesNotReopenWatchClosedInCrm()
    {
        await using var db = CreateDb();
        var (worker, person, response) = Seed(db);
        var card = new CrmCandidateCardEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = response.Id,
            OfficeId = worker.OfficeId,
            Stage = CrmStages.Lead,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            StageChangedAtUtc = DateTime.UtcNow
        };
        db.Add(card);
        db.SaveChanges();

        var accountId = Guid.NewGuid();
        var service = new CandidatePhoneWatchService(db);
        await service.UpsertAsync(
            worker,
            Candidate(accountId, person, DateTime.UtcNow.AddHours(-1)),
            person.Id,
            response.Id,
            "79001111111",
            WorkerCandidateOperationKinds.NewResponse,
            CancellationToken.None);
        await service.CloseForCardsAsync([card], DateTime.UtcNow, CancellationToken.None);

        // Опоздавшая публикация воркера (смена номера) не открывает закрытое в CRM окно.
        await service.UpsertAsync(
            worker,
            Candidate(accountId, person, DateTime.UtcNow),
            person.Id,
            response.Id,
            "79002222222",
            WorkerCandidateOperationKinds.PhoneChanged,
            CancellationToken.None);

        var watch = await db.CandidatePhoneWatches.SingleAsync();
        Assert.Equal(CandidatePhoneWatchStates.ClosedInCrm, watch.State);
    }

    [Fact]
    public async Task LookupAsync_ReturnsClosedInCrmFlagAndExcludesWatchFromOpen()
    {
        await using var db = CreateDb();
        var (worker, person, response) = Seed(db);
        var card = new CrmCandidateCardEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = response.Id,
            OfficeId = worker.OfficeId,
            Stage = CrmStages.Lead,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            StageChangedAtUtc = DateTime.UtcNow
        };
        db.Add(card);
        db.SaveChanges();

        var accountId = Guid.NewGuid();
        var service = new CandidatePhoneWatchService(db);
        await service.UpsertAsync(
            worker,
            Candidate(accountId, person, DateTime.UtcNow.AddHours(-1)),
            person.Id,
            response.Id,
            "79001111111",
            WorkerCandidateOperationKinds.NewResponse,
            CancellationToken.None);
        await service.CloseForCardsAsync([card], DateTime.UtcNow, CancellationToken.None);
        db.SaveChanges();
        var watch = await db.CandidatePhoneWatches.SingleAsync();

        var lookup = new CandidateLookupService(
            db,
            new CandidatePersonMatchService(db),
            new CandidatePhoneWatchService(db));
        var result = await lookup.LookupAsync(
            worker.Id,
            new WorkerCandidateLookupRequest(
                accountId,
                "PerAvitoAccount",
                [watch.PublishedSourceResponseId],
                [],
                IncludeSourceResponseMetadata: true,
                AvitoSubProfileId: "sub-1",
                OpenPhoneWatchHours: 120));

        Assert.NotNull(result);
        var known = Assert.Single(result!.ExistingSourceResponses!);
        Assert.True(known.WatchClosedInCrm);
        Assert.Empty(result.OpenPhoneWatches!);
    }

    private static WorkerCandidateDto Candidate(
        Guid accountId,
        CandidatePersonEntity person,
        DateTime collectedAt) =>
        new(
            accountId,
            "Avito",
            "Avito",
            "avito:real-response",
            "card-fingerprint",
            person.FullName,
            person.Age,
            null,
            "+79001111111",
            person.City,
            "Охранник",
            "",
            "",
            "sub-1",
            "",
            "",
            collectedAt,
            CollectedAt: collectedAt);

    private static (WorkerEntity Worker, CandidatePersonEntity Person, CandidateResponseEntity Response) Seed(
        OrbitaDbContext db)
    {
        var office = new OfficeEntity
        {
            Id = Guid.NewGuid(),
            Name = "Office",
            RegistrationSecretHash = "hash",
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        var worker = new WorkerEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = office.Id,
            DisplayName = "worker",
            MachineName = "pc",
            ApiKeyHash = "hash",
            AppVersion = "1.0",
            MonitoringStatus = "Running",
            PhoneUnchangedHours = 120,
            CreatedAtUtc = DateTime.UtcNow
        };
        var person = TestCandidatePersonFactory.CreatePerson(
            office.Id,
            fullName: "Иванов Иван Иванович",
            firstName: "Иван",
            lastName: "Иванов",
            middleName: "Иванович",
            age: 35,
            city: "Самара",
            phoneRaw: "+79001111111",
            phoneNormalized: "79001111111");
        var response = TestCandidatePersonFactory.CreateResponse(
            office.Id,
            person.Id,
            worker.Id,
            phone: "79001111111",
            sourceResponseId: "avito:real-response",
            fullName: person.FullName,
            age: person.Age,
            city: person.City);
        db.AddRange(office, worker, person, response);
        db.SaveChanges();
        return (worker, person, response);
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }
}
