using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;
using CandidateParser = Orbita.Api.Services.CandidateParser;

namespace Orbita.Tests;

public sealed partial class CandidateIngestionServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task IngestBatchAsync_ExistingResponse_BackfillsVacancyUrl()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var accountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var person = TestCandidatePersonFactory.CreatePerson(OfficeId, fullName: "Иванов Иван", firstName: "Иван", lastName: "Иванов");
        var response = TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            sourceResponseId: "existing-response",
            fullName: "Иванов Иван");
        response.AccountId = accountId;
        response.VacancyUrl = string.Empty;
        response.SourceUrl = string.Empty;
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(response);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var request = new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                accountId,
                "acc",
                "Avito",
                "existing-response",
                "",
                "Иванов Иван",
                25,
                null,
                "+7 (900) 111-11-11",
                "Москва",
                "Охранник",
                "https://www.avito.ru/example/vacancy",
                "",
                "",
                "",
                "",
                DateTime.UtcNow,
                Citizenship: "Россия")
        ]);

        await sut.IngestBatchAsync(WorkerId, request);

        var stored = await db.CandidateResponses.SingleAsync(x => x.Id == response.Id);
        Assert.Equal("https://www.avito.ru/example/vacancy", stored.VacancyUrl);
        Assert.Equal("https://www.avito.ru/example/vacancy", stored.SourceUrl);
        Assert.Equal("Россия", stored.Citizenship);
    }

    [Fact]
    public async Task IngestBatchAsync_ExistingWatch_RefreshesUnlockedAvitoFields()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var accountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var person = TestCandidatePersonFactory.CreatePerson(OfficeId, fullName: "Ахмед аминов русланов", firstName: "Ахмед", lastName: "Аминов", city: "");
        var response = TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            sourceResponseId: "phone-watch:watch1",
            fullName: "Ахмед аминов русланов",
            age: null,
            city: "");
        response.AccountId = accountId;
        response.Vacancy = string.Empty;
        response.Gender = string.Empty;
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(response);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        await sut.IngestBatchAsync(WorkerId, new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                accountId,
                "acc",
                "Avito",
                "phone-watch:watch1",
                "",
                "Ахмед аминов русланов",
                28,
                CandidateGenders.Male,
                "+7 (900) 111-11-11",
                "Батайск",
                "Разнорабочий вахта",
                "https://www.avito.ru/8186548533",
                "",
                "",
                "Мужчина · 28 лет · Гражданство: Россия",
                "",
                DateTime.UtcNow,
                Citizenship: "Россия")
        ]));

        var stored = await db.CandidateResponses.SingleAsync(x => x.Id == response.Id);
        Assert.Equal("Батайск", stored.City);
        Assert.Equal("Разнорабочий вахта", stored.Vacancy);
        Assert.Equal(28, stored.Age);
        Assert.Equal(CandidateGenders.Male, stored.Gender);
        Assert.Equal("https://www.avito.ru/8186548533", stored.VacancyUrl);
        Assert.Equal("Россия", stored.Citizenship);

        var storedPerson = await db.CandidatePersons.SingleAsync(x => x.Id == person.Id);
        Assert.Equal("Батайск", storedPerson.City);
        Assert.Equal(28, storedPerson.Age);
    }

    [Fact]
    public async Task IngestBatchAsync_ExistingWatch_DoesNotOverwriteOperatorLockedFields()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var accountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var person = TestCandidatePersonFactory.CreatePerson(OfficeId, city: "Казань");
        var response = TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            sourceResponseId: "phone-watch:locked",
            city: "Казань");
        response.AccountId = accountId;
        response.City = string.Empty;
        response.Vacancy = string.Empty;
        response.Age = null;
        response.OperatorLockedFields = ResponseOperatorLocks.Add(
            ResponseOperatorLocks.Add(null, ResponseOperatorLocks.City),
            ResponseOperatorLocks.Age);
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(response);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        await sut.IngestBatchAsync(WorkerId, new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                accountId,
                "acc",
                "Avito",
                "phone-watch:locked",
                "",
                "Test User",
                28,
                null,
                "79001111111",
                "Батайск",
                "Разнорабочий вахта",
                "",
                "",
                "",
                "",
                "",
                DateTime.UtcNow)
        ]));

        var stored = await db.CandidateResponses.SingleAsync(x => x.Id == response.Id);
        Assert.Equal(string.Empty, stored.City);
        Assert.Null(stored.Age);
        Assert.Equal("Разнорабочий вахта", stored.Vacancy);

        var storedPerson = await db.CandidatePersons.SingleAsync(x => x.Id == person.Id);
        Assert.Equal("Казань", storedPerson.City);
        Assert.Equal(25, storedPerson.Age);
    }

    [Fact]
    public async Task IngestBatchAsync_ExistingWatch_BackfillsCreatedAtFromChatWhenFallbackWasUsed()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var accountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var collectedAt = new DateTime(2026, 8, 21, 14, 22, 56, DateTimeKind.Utc);
        var chatAt = new DateTime(2026, 8, 14, 8, 15, 0, DateTimeKind.Utc);
        var person = TestCandidatePersonFactory.CreatePerson(OfficeId, createdAtUtc: collectedAt);
        var response = TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            sourceResponseId: "phone-watch:chat-date",
            createdAt: collectedAt);
        response.AccountId = accountId;
        response.ChatMessagesJson = string.Empty;
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(response);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        await sut.IngestBatchAsync(WorkerId, new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                accountId,
                "acc",
                "Avito",
                "phone-watch:chat-date",
                "",
                "Test User",
                25,
                null,
                "79001111111",
                "Москва",
                "Охранник",
                "",
                "",
                "",
                "",
                """[{"text":"Кандидат откликнулся на вакансию. Его данные сохранились в разделе «Отклики».","at":"2026-08-14T08:15:00Z","side":"left","isPlatform":true}]""",
                chatAt,
                CollectedAt: DateTime.UtcNow)
        ]));

        var stored = await db.CandidateResponses.SingleAsync(x => x.Id == response.Id);
        Assert.Equal(chatAt, stored.CreatedAt);
        Assert.Equal(collectedAt, stored.CollectedAt);
    }

    [Fact]
    public async Task IngestBatchAsync_ExistingWatch_BackfillsCreatedAtFromChatJsonWhenWorkerSentFallback()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var accountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var collectedAt = new DateTime(2026, 8, 21, 14, 22, 56, DateTimeKind.Utc);
        var laterPassAt = new DateTime(2026, 8, 21, 18, 0, 0, DateTimeKind.Utc);
        var person = TestCandidatePersonFactory.CreatePerson(OfficeId, createdAtUtc: collectedAt);
        var response = TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            sourceResponseId: "phone-watch:chat-json",
            createdAt: collectedAt);
        response.AccountId = accountId;
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(response);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        await sut.IngestBatchAsync(WorkerId, new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                accountId,
                "acc",
                "Avito",
                "phone-watch:chat-json",
                "",
                "Test User",
                25,
                null,
                "79001111111",
                "Москва",
                "Охранник",
                "",
                "",
                "",
                "",
                """[{"text":"Кандидат откликнулся на вакансию.","at":"2026-08-14T08:15:00Z","side":"left","isPlatform":true}]""",
                laterPassAt,
                CollectedAt: laterPassAt)
        ]));

        var stored = await db.CandidateResponses.SingleAsync(x => x.Id == response.Id);
        Assert.Equal(new DateTime(2026, 8, 14, 8, 15, 0, DateTimeKind.Utc), stored.CreatedAt);
        Assert.Equal(collectedAt, stored.CollectedAt);
    }

    [Fact]
    public async Task IngestBatchAsync_ExistingWatch_DoesNotOverwriteKnownCreatedAt()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var accountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var collectedAt = new DateTime(2026, 8, 21, 14, 22, 56, DateTimeKind.Utc);
        var knownAt = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);
        var person = TestCandidatePersonFactory.CreatePerson(OfficeId, createdAtUtc: collectedAt);
        var response = TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            sourceResponseId: "phone-watch:known-date",
            createdAt: knownAt);
        response.AccountId = accountId;
        response.CollectedAt = collectedAt;
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(response);
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        await sut.IngestBatchAsync(WorkerId, new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                accountId,
                "acc",
                "Avito",
                "phone-watch:known-date",
                "",
                "Test User",
                25,
                null,
                "79001111111",
                "Москва",
                "Охранник",
                "",
                "",
                "",
                "",
                """[{"text":"Кандидат откликнулся на вакансию.","at":"2026-08-14T08:15:00Z","side":"left","isPlatform":true}]""",
                new DateTime(2026, 8, 14, 8, 15, 0, DateTimeKind.Utc),
                CollectedAt: DateTime.UtcNow)
        ]));

        var stored = await db.CandidateResponses.SingleAsync(x => x.Id == response.Id);
        Assert.Equal(knownAt, stored.CreatedAt);
        Assert.Equal(collectedAt, stored.CollectedAt);
    }

    [Fact]
    public async Task IngestBatchAsync_PhoneChangedMetric_DoesNotMarkAsDuplicate()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var person = TestCandidatePersonFactory.CreatePerson(
            OfficeId,
            fullName: "Гор Олег Александрович",
            firstName: "Олег",
            lastName: "Гор",
            middleName: "Александрович",
            age: 66,
            city: "рабочий поселок Чик",
            phoneRaw: "+79930099416",
            phoneNormalized: "79930099416");
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            phone: "79930099416",
            sourceResponseId: "existing-source",
            fullName: "Гор Олег Александрович",
            age: 66,
            city: "рабочий поселок Чик"));
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var request = new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                "acc",
                "Avito",
                "phone-chg:abc",
                "",
                "Гор Олег Александрович",
                66,
                null,
                "+79910001122",
                "рабочий поселок Чик",
                "Курьер",
                "",
                "",
                "",
                "",
                "",
                DateTime.UtcNow,
                AvitoSubProfileName: "",
                CollectedAt: default,
                PhoneMetricKind: ResponsePhoneMetricKinds.PhoneChanged,
                PreviousPhoneRaw: "+79930099416",
                PreviousPhoneNormalized: "79930099416",
                PhoneChangedAtUtc: DateTime.UtcNow)
        ]);

        var result = await sut.IngestBatchAsync(WorkerId, request);

        Assert.Equal(0, result.SkippedDuplicates);
        Assert.Equal(ResponseStatuses.ActionRequired, result.Items[0].Status);

        var stored = await db.CandidateResponses.SingleAsync(x => x.SourceResponseId == "phone-chg:abc");
        Assert.Equal(ResponsePhoneMetricKinds.PhoneChanged, stored.PhoneMetricKind);
        Assert.Equal("79930099416", stored.PreviousPhoneNormalized);
        Assert.False(stored.IsLocalDuplicate);
        Assert.Equal(person.Id, stored.PersonId);

        var phoneHistory = await db.CandidatePhoneHistory
            .Where(x => x.PersonId == person.Id)
            .OrderBy(x => x.RecordedAtUtc)
            .Select(x => x.PhoneNormalized)
            .ToListAsync();
        Assert.Equal(["79930099416", "79910001122"], phoneHistory);
    }

    [Fact]
    public async Task IngestBatchAsync_PhoneUnchangedMetric_WithoutPersonMatch_DoesNotMarkAsDuplicate()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var sut = CreateService(db);
        var request = new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                "acc",
                "Avito",
                "phone-stable:abc",
                "",
                "Гор Олег Александрович",
                66,
                null,
                "+79930099416",
                "рабочий поселок Чик",
                "Курьер",
                "",
                "",
                "",
                "",
                "",
                DateTime.UtcNow,
                AvitoSubProfileName: "",
                CollectedAt: default,
                PhoneMetricKind: ResponsePhoneMetricKinds.PhoneUnchanged,
                PhoneUnchangedHours: 48)
        ]);

        var result = await sut.IngestBatchAsync(WorkerId, request);

        Assert.Equal(0, result.SkippedDuplicates);
        Assert.Equal(ResponseStatuses.ActionRequired, result.Items[0].Status);

        var stored = await db.CandidateResponses.SingleAsync(x => x.SourceResponseId == "phone-stable:abc");
        Assert.Equal(ResponsePhoneMetricKinds.PhoneUnchanged, stored.PhoneMetricKind);
        Assert.Equal(48, stored.PhoneUnchangedHours);
        Assert.False(stored.IsLocalDuplicate);
        Assert.Equal(ResponseStatuses.ActionRequired, stored.Status);
    }

    [Fact]
    public async Task IngestBatchAsync_PhoneChangedOnSentResponse_UpdatesSameResponseAndAppendsHistory()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var accountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var person = TestCandidatePersonFactory.CreatePerson(
            OfficeId,
            fullName: "Гор Олег Александрович",
            firstName: "Олег",
            lastName: "Гор",
            middleName: "Александрович",
            age: 66,
            city: "рабочий поселок Чик",
            phoneRaw: "+79930099416",
            phoneNormalized: "79930099416");
        var response = TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            phone: "79930099416",
            sourceResponseId: "phone-watch:deadbeef",
            fullName: "Гор Олег Александрович",
            age: 66,
            city: "рабочий поселок Чик");
        response.AccountId = accountId;
        response.Status = ResponseStatuses.Sent;
        response.PhoneRaw = "+79930099416";
        response.City = string.Empty;
        response.Vacancy = string.Empty;
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(response);
        db.CandidatePhoneHistory.Add(new CandidatePhoneHistoryEntity
        {
            Id = Guid.NewGuid(),
            PersonId = person.Id,
            ResponseId = response.Id,
            PhoneRaw = "+79930099416",
            PhoneNormalized = "79930099416",
            RecordedAtUtc = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var request = new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                accountId,
                "acc",
                "Avito",
                "phone-watch:deadbeef",
                "",
                "Гор Олег Александрович",
                66,
                null,
                "+79910001122",
                "рабочий поселок Чик",
                "Курьер",
                "",
                "",
                "",
                "",
                "",
                DateTime.UtcNow,
                AvitoSubProfileName: "",
                CollectedAt: default,
                PhoneMetricKind: ResponsePhoneMetricKinds.PhoneChanged,
                PreviousPhoneRaw: "+79930099416",
                PreviousPhoneNormalized: "79930099416",
                PhoneChangedAtUtc: DateTime.UtcNow)
        ]);

        var result = await sut.IngestBatchAsync(WorkerId, request);

        Assert.Equal(1, await db.CandidateResponses.CountAsync());
        var stored = await db.CandidateResponses.SingleAsync(x => x.Id == response.Id);
        Assert.Equal("79910001122", stored.PhoneNormalized);
        Assert.Equal("рабочий поселок Чик", stored.City);
        Assert.Equal("Курьер", stored.Vacancy);
        Assert.Equal(ResponsePhoneMetricKinds.PhoneChanged, stored.PhoneMetricKind);
        Assert.Equal("79930099416", stored.PreviousPhoneNormalized);
        Assert.Equal(ResponseStatuses.Sent, stored.Status);

        var history = await db.CandidatePhoneHistory
            .Where(x => x.PersonId == person.Id)
            .OrderBy(x => x.RecordedAtUtc)
            .ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.Equal(response.Id, h.ResponseId));
        Assert.Equal(["79930099416", "79910001122"], history.Select(h => h.PhoneNormalized).ToList());
        Assert.Equal(response.Id, result.Items[0].Id);
    }

    [Fact]
    public async Task IngestBatchAsync_SamePersonDifferentPhone_StoresDuplicateStatus()
    {
        await using var db = CreateDb();
        SeedWorker(db);

        var person = TestCandidatePersonFactory.CreatePerson(
            OfficeId,
            fullName: "Гор Олег Александрович",
            firstName: "Олег",
            lastName: "Гор",
            middleName: "Александрович",
            age: 66,
            city: "рабочий поселок Чик",
            phoneRaw: "+79930099416",
            phoneNormalized: "79930099416");
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            phone: "79930099416",
            sourceResponseId: "existing-source",
            fullName: "Гор Олег Александрович",
            age: 66,
            city: "рабочий поселок Чик"));
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var request = new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                "acc",
                "Avito",
                "new-source-id",
                "",
                "Гор Олег Александрович",
                66,
                null,
                "+79910001122",
                "рабочий поселок Чик",
                "Курьер",
                "",
                "",
                "",
                "",
                "",
                DateTime.UtcNow)
        ]);

        var result = await sut.IngestBatchAsync(WorkerId, request);

        Assert.Equal(1, result.SkippedDuplicates);
        Assert.Equal(ResponseStatuses.Duplicate, result.Items[0].Status);

        var stored = await db.CandidateResponses.SingleAsync(x => x.SourceResponseId == "new-source-id");
        Assert.Equal(ResponseStatuses.Duplicate, stored.Status);
        Assert.True(stored.IsLocalDuplicate);
        Assert.Equal(person.Id, stored.PersonId);
        Assert.Empty(await db.CandidatePhoneWatches.ToListAsync());
    }

    [Fact]
    public async Task IngestBatchAsync_WatchRefreshForKnownPerson_DoesNotCreateDuplicateResponse()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var person = TestCandidatePersonFactory.CreatePerson(
            OfficeId,
            fullName: "Иванов Иван Иванович",
            firstName: "Иван",
            lastName: "Иванов",
            middleName: "Иванович",
            age: 35,
            city: "Самара",
            phoneRaw: "+79001111111",
            phoneNormalized: "79001111111");
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            phone: "79001111111",
            sourceResponseId: "original-response",
            fullName: person.FullName,
            age: person.Age,
            city: person.City));
        await db.SaveChangesAsync();

        var result = await CreateService(db).IngestBatchAsync(
            WorkerId,
            new WorkerCandidateBatchRequest([
                new WorkerCandidateDto(
                    Guid.NewGuid(),
                    "other-account",
                    "Avito",
                    "phone-watch:new-account",
                    "fingerprint",
                    person.FullName,
                    person.Age,
                    null,
                    "+79001111111",
                    person.City,
                    "Охранник",
                    "",
                    "https://www.avito.ru/messenger",
                    "sub-1",
                    "",
                    "[{\"text\":\"обновлённый чат\"}]",
                    DateTime.UtcNow,
                    OperationKind: WorkerCandidateOperationKinds.WatchRefresh)
            ]));

        Assert.Equal(WorkerCandidateIngestionOutcomes.WatchUpdated, Assert.Single(result.Items).Outcome);
        Assert.Equal(0, result.SkippedDuplicates);
        Assert.Single(await db.CandidateResponses.ToListAsync());
    }

    [Fact]
    public async Task IngestBatchAsync_LegacyPhoneWatchWithoutOperationKind_DoesNotCreateDuplicateResponse()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var person = TestCandidatePersonFactory.CreatePerson(
            OfficeId,
            fullName: "Сидоров Сидор Сидорович",
            firstName: "Сидор",
            lastName: "Сидоров",
            middleName: "Сидорович",
            phoneRaw: "+79004444444",
            phoneNormalized: "79004444444");
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            phone: "79004444444",
            sourceResponseId: "canonical-legacy",
            fullName: person.FullName));
        await db.SaveChangesAsync();

        var result = await CreateService(db).IngestBatchAsync(
            WorkerId,
            new WorkerCandidateBatchRequest([
                new WorkerCandidateDto(
                    Guid.NewGuid(),
                    "legacy-account",
                    "Avito",
                    "phone-watch:legacy",
                    "",
                    person.FullName,
                    person.Age,
                    null,
                    "+79004444444",
                    person.City,
                    "Охранник",
                    "",
                    "",
                    "sub-legacy",
                    "",
                    "",
                    DateTime.UtcNow)
            ]));

        Assert.Equal(WorkerCandidateIngestionOutcomes.WatchUpdated, Assert.Single(result.Items).Outcome);
        Assert.Equal(0, result.SkippedDuplicates);
        Assert.Single(await db.CandidateResponses.ToListAsync());
        Assert.Single(await db.CandidatePhoneWatches.ToListAsync());
    }

    [Fact]
    public async Task IngestBatchAsync_PhoneChangedForKnownPerson_UpdatesCanonicalResponse()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var person = TestCandidatePersonFactory.CreatePerson(
            OfficeId,
            fullName: "Петров Пётр Петрович",
            firstName: "Пётр",
            lastName: "Петров",
            middleName: "Петрович",
            age: 42,
            city: "Самара",
            phoneRaw: "+79002222222",
            phoneNormalized: "79002222222");
        var canonical = TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            phone: "79002222222",
            sourceResponseId: "canonical-response",
            fullName: person.FullName,
            age: person.Age,
            city: person.City);
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(canonical);
        await db.SaveChangesAsync();

        var result = await CreateService(db).IngestBatchAsync(
            WorkerId,
            new WorkerCandidateBatchRequest([
                new WorkerCandidateDto(
                    Guid.NewGuid(),
                    "other-account",
                    "Avito",
                    "phone-watch:changed",
                    "fingerprint",
                    person.FullName,
                    person.Age,
                    null,
                    "+79003333333",
                    person.City,
                    "Охранник",
                    "",
                    "",
                    "sub-1",
                    "",
                    "",
                    DateTime.UtcNow,
                    PreviousPhoneRaw: "+79002222222",
                    PreviousPhoneNormalized: "79002222222",
                    PhoneChangedAtUtc: DateTime.UtcNow,
                    OperationKind: WorkerCandidateOperationKinds.PhoneChanged)
            ]));

        Assert.Equal(WorkerCandidateIngestionOutcomes.PhoneChanged, Assert.Single(result.Items).Outcome);
        Assert.Equal(0, result.SkippedDuplicates);
        Assert.Single(await db.CandidateResponses.ToListAsync());
        var stored = await db.CandidateResponses.SingleAsync(x => x.Id == canonical.Id);
        Assert.Equal("79003333333", stored.PhoneNormalized);
        Assert.Equal(ResponsePhoneMetricKinds.PhoneChanged, stored.PhoneMetricKind);
    }

    [Fact]
    public async Task IngestBatchAsync_SamePersonDifferentCitySamePhone_StoresDuplicateStatus()
    {
        await using var db = CreateDb();
        SeedWorker(db);

        var person = TestCandidatePersonFactory.CreatePerson(
            OfficeId,
            fullName: "Узбеков Шовкат Джумазарович",
            firstName: "Шовкат",
            lastName: "Узбеков",
            middleName: "Джумазарович",
            age: 42,
            city: "Серпухов",
            phoneRaw: "+79999213355",
            phoneNormalized: "79999213355");
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            WorkerId,
            phone: "79999213355",
            sourceResponseId: "serpukhov-source",
            fullName: "Узбеков Шовкат Джумазарович",
            age: 42,
            city: "Серпухов"));
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var request = new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                "acc-2",
                "Avito",
                "protvino-source",
                "",
                "Узбеков Шовкат Джумазарович",
                42,
                null,
                "+7 999 921-33-55",
                "Протвино",
                "Разнорабочий",
                "",
                "",
                "",
                "",
                "",
                DateTime.UtcNow)
        ]);

        var result = await sut.IngestBatchAsync(WorkerId, request);

        Assert.Equal(1, result.SkippedDuplicates);
        Assert.Equal(ResponseStatuses.Duplicate, result.Items[0].Status);

        var stored = await db.CandidateResponses.SingleAsync(x => x.SourceResponseId == "protvino-source");
        Assert.Equal(ResponseStatuses.Duplicate, stored.Status);
        Assert.True(stored.IsLocalDuplicate);
        Assert.Equal(person.Id, stored.PersonId);
    }

    [Fact]
    public async Task IngestBatchAsync_AutoDistributionDisabled_StoresActionRequired()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);

        var sut = CreateService(db);
        var request = new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                "acc",
                "Avito",
                "disabled-auto-source",
                "",
                "New User",
                25,
                null,
                "+7 (900) 222-22-22",
                "Москва",
                "Курьер",
                "",
                "",
                "",
                "",
                "",
                DateTime.UtcNow)
        ]);

        var result = await sut.IngestBatchAsync(WorkerId, request);

        Assert.Equal(0, result.Ingested);
        Assert.Equal(ResponseStatuses.ActionRequired, result.Items[0].Status);
        Assert.Equal("Ожидает действия оператора.", result.Items[0].ErrorMessage);

        var stored = await db.CandidateResponses.SingleAsync(x => x.SourceResponseId == "disabled-auto-source");
        Assert.Equal(ResponseStatuses.ActionRequired, stored.Status);
        Assert.Equal("Ожидает действия оператора.", stored.ErrorMessage);
        Assert.NotEqual(Guid.Empty, stored.PersonId);
    }

    [Fact]
    public async Task IngestBatchAsync_ValidAvatarPayload_PersistsDownloadedImage()
    {
        await using var db = CreateDb();
        SeedWorker(db, autoDistributionEnabled: false);
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

        var request = new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                "acc",
                "Avito",
                "avatar-source",
                "",
                "Иванов Иван",
                25,
                "male",
                "+7 (900) 222-22-22",
                "Москва",
                "Курьер",
                "",
                "",
                "",
                "",
                "",
                DateTime.UtcNow,
                AvatarContentType: "image/png",
                AvatarImageBase64: Convert.ToBase64String(png))
        ]);

        await CreateService(db).IngestBatchAsync(WorkerId, request);

        var stored = await db.CandidateResponses.SingleAsync(x => x.SourceResponseId == "avatar-source");
        Assert.Equal("image/png", stored.AvatarContentType);
        Assert.Equal(png, stored.AvatarImage);
    }

    private static CandidateIngestionService CreateService(OrbitaDbContext db)
    {
        var bitrixOptions = Options.Create(new OrbitaBitrixSettings { CheckDuplicatesInBitrix = false });
        var personMatch = new CandidatePersonMatchService(db);
        var personPhone = new CandidatePersonPhoneService(db);
        var audit = new PanelAuditService(db);
        var duplicateService = new CandidateDuplicateService(db, personMatch, new BitrixClient(new HttpClientFactoryStub(), new CandidateParser()));
        var bitrixInstanceService = new BitrixInstanceService(db, null!, null!, bitrixOptions, audit);
        var distributionRoute = new DistributionRouteService(db, audit);
        var distributionEngine = new DistributionEngine(db);
        var bitrixDuplicateCheck = new BitrixDuplicateCheckAllService(
            bitrixInstanceService,
            new BitrixClient(new HttpClientFactoryStub(), new CandidateParser()),
            bitrixOptions);
        var bitrixSend = new CandidateBitrixSendService(
            db,
            bitrixInstanceService,
            new BitrixClient(new HttpClientFactoryStub(), new CandidateParser()),
            bitrixOptions);
        var deliveries = new ResponseBitrixDeliveryService(db);
        var leadExportQuota = new LeadExportQuotaService(db);
        var autoDistribution = new CandidateAutoDistributionService(bitrixDuplicateCheck, bitrixSend, deliveries, leadExportQuota);
        var cacheInvalidator = new ResponseCacheInvalidator(
            new NoopQueryCache(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ResponseCacheInvalidator>.Instance);
        var manualSend = new ManualBitrixSendService(db, duplicateService, bitrixDuplicateCheck, bitrixSend, deliveries, new NoopPanelRealtimeNotifier(), cacheInvalidator);
        var crmWorkspace = new CrmWorkspaceService(
            db,
            /* users */ null!,
            new CrmLeadDistributionService(db, null!));
        // CrmWorkspaceService needs UserManager only for board ops; delivery path uses TryCreateCard + LeadDistribution.
        // For ingestion tests CRM auto is off by default — ResponseDeliveryService still constructed.
        var delivery = new ResponseDeliveryService(
            db,
            crmWorkspace,
            distributionEngine,
            autoDistribution,
            manualSend,
            duplicateService,
            new NoopPanelRealtimeNotifier(),
            cacheInvalidator);

        return new CandidateIngestionService(
            db,
            new PhoneNormalizer(),
            new CandidateParser(),
            personMatch,
            personPhone,
            new CandidatePhoneWatchService(db),
            distributionEngine,
            autoDistribution,
            manualSend,
            delivery,
            bitrixOptions,
            new NoopPanelRealtimeNotifier());
    }

    private sealed class NoopQueryCache : IOrbitaQueryCache
    {
        public Task<T> GetOrCreateAsync<T>(OrbitaCacheDomain domain, Guid? officeId, string? audience,
            object? parameters, OrbitaCachePolicy policy, Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default) => factory(cancellationToken);
        public Task<T> GetOrCreateDistributedAsync<T>(OrbitaCacheDomain domain, Guid? officeId, string? audience,
            object? parameters, OrbitaCachePolicy policy, Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default) => factory(cancellationToken);
        public Task InvalidateAsync(IReadOnlyList<PanelChangeKind> changes, Guid? officeId) => Task.CompletedTask;
        public void ClearLocalVersion(OrbitaCacheDomain domain, Guid? officeId) { }
    }

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedWorker(OrbitaDbContext db, bool autoDistributionEnabled = true)
    {
        db.Offices.Add(new OfficeEntity
        {
            Id = OfficeId,
            Name = "Test Office",
            RegistrationSecretHash = "hash",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            BitrixTransmissionEnabled = autoDistributionEnabled
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
            CreatedAtUtc = DateTime.UtcNow,
            AutoDeliverToCrm = false,
            AutoDeliverToBitrix = autoDistributionEnabled
        });
        db.DistributionRoutes.Add(new DistributionRouteEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            IsAutoDistributionEnabled = autoDistributionEnabled,
            UpdatedAtUtc = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
