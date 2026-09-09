using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CandidateLookupServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherOfficeId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task LookupAsync_BatchPhones_MatchesAcrossSubProfilesAndAccounts()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        var otherAccountId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        db.CandidateResponses.AddRange(
            NewResponse("79001111111", "sub-a"),
            NewResponse("79002222222", "sub-b", otherAccountId));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "PerAvitoAccount",
                [],
                ["79001111111", "79002222222"],
                AvitoSubProfileId: "sub-a"));

        Assert.NotNull(result);
        Assert.Equal(["79001111111", "79002222222"], result!.ExistingPhones.OrderBy(static x => x).ToArray());
        Assert.Empty(result.MatchedProfileIndexes);
    }

    [Fact]
    public async Task LookupAsync_BatchPhones_MatchesLegacyEmptySubProfile()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        db.CandidateResponses.Add(NewResponse("79004444444", ""));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "PerAvitoAccount",
                [],
                ["79004444444"],
                AvitoSubProfileId: "sub-a"));

        Assert.NotNull(result);
        Assert.Equal(["79004444444"], result!.ExistingPhones);
    }

    [Fact]
    public async Task LookupAsync_BatchPhones_IgnoresResponsesOlderThanSixMonths()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        db.CandidateResponses.Add(NewResponse(
            "79006666666",
            "sub-a",
            createdAt: DateTime.UtcNow.AddMonths(-7)));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "GlobalAcrossAllAccounts",
                [],
                ["79006666666"]));

        Assert.NotNull(result);
        Assert.Empty(result!.ExistingPhones);
    }

    [Fact]
    public async Task LookupAsync_SourceResponseIds_IgnoresResponsesOlderThanSixMonths()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        const string sourceResponseId = "avito-response-old";
        db.CandidateResponses.Add(NewResponse(
            "79007777777",
            "sub-a",
            sourceResponseId: sourceResponseId,
            createdAt: DateTime.UtcNow.AddMonths(-7)));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "PerAvitoAccount",
                [sourceResponseId],
                []));

        Assert.NotNull(result);
        Assert.Empty(result!.ExistingSourceResponseIds);
    }

    [Fact]
    public async Task LookupAsync_SourceResponseMetadata_ReturnsNewestPhoneWatchFirst()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        var older = DateTime.UtcNow.AddDays(-2);
        var newer = DateTime.UtcNow.AddHours(-3);
        db.CandidateResponses.AddRange(
            NewResponse("79007770001", "sub-a", sourceResponseId: "phone-watch:older", createdAt: older),
            NewResponse("79007770002", "sub-a", sourceResponseId: "phone-watch:newer", createdAt: newer));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "PerAvitoAccount",
                ["phone-watch:older", "phone-watch:newer"],
                [],
                IncludeSourceResponseMetadata: true));

        Assert.NotNull(result);
        Assert.Equal(
            ["phone-watch:newer", "phone-watch:older"],
            result!.ExistingSourceResponses.Select(static x => x.SourceResponseId).ToArray());
        Assert.Equal([newer, older], result.ExistingSourceResponses.Select(static x => x.CollectedAt).ToArray());
    }

    [Fact]
    public async Task LookupAsync_OpenPhoneWatches_ReturnsActiveWatchesForRequestedSubProfile()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        var activeAt = DateTime.UtcNow.AddHours(-90);
        db.CandidateResponses.AddRange(
            NewResponse(
                "79947809504",
                "sub-a",
                sourceResponseId: "phone-watch:active",
                createdAt: activeAt,
                fullName: "Гафуров сахобилддин Асхобиддинович"),
            NewResponse(
                "79001111112",
                "sub-a",
                sourceResponseId: "phone-watch:expired",
                createdAt: DateTime.UtcNow.AddHours(-121),
                fullName: "Старое наблюдение"),
            NewResponse(
                "79001111113",
                "sub-b",
                sourceResponseId: "phone-watch:other-sub",
                createdAt: DateTime.UtcNow.AddHours(-1),
                fullName: "Другой субпрофиль"));
        await db.SaveChangesAsync();

        var result = await CreateSut(db).LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "PerAvitoAccount",
                [],
                [],
                AvitoSubProfileId: "sub-a",
                OpenPhoneWatchHours: 120));

        var watch = Assert.Single(result!.OpenPhoneWatches!);
        Assert.Equal("phone-watch:active", watch.SourceResponseId);
        Assert.Equal("Гафуров сахобилддин Асхобиддинович", watch.FullName);
        Assert.Equal(activeAt, watch.CollectedAt);
    }

    [Fact]
    public async Task LookupAsync_CardFingerprints_MatchesStoredFingerprint()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        const string fingerprint = "avito-card:msg:test-channel";
        db.CandidateResponses.Add(NewResponse(
            "79008888888",
            "sub-a",
            cardFingerprint: fingerprint));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "PerAvitoAccount",
                [],
                [],
                AvitoSubProfileId: "sub-a",
                CardFingerprints: [fingerprint, "avito-card:unknown"]));

        Assert.NotNull(result);
        Assert.Equal([fingerprint], result!.ExistingCardFingerprints);
    }

    [Fact]
    public async Task LookupAsync_CardFingerprints_FiltersBySubProfile()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        const string fingerprint = "avito-card:msg:sub-b-only";
        db.CandidateResponses.Add(NewResponse(
            "79009999999",
            "sub-b",
            cardFingerprint: fingerprint));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "PerAvitoAccount",
                [],
                [],
                AvitoSubProfileId: "sub-a",
                CardFingerprints: [fingerprint]));

        Assert.NotNull(result);
        Assert.Empty(result!.ExistingCardFingerprints);
    }

    [Fact]
    public async Task LookupAsync_CardFingerprints_RecomputesLegacyRowsWithNormalizedAge()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        var legacyFingerprint = AvitoResponseCardFingerprint.Build(
            "Иван Иванов",
            "Слесарь",
            "Москва",
            "https://www.avito.ru/moskva/vakansii/slesar_8072057107",
            null,
            AvitoResponseCardFingerprint.NormalizeAgeText(null, 42));
        db.CandidateResponses.Add(NewResponse(
            "79001010101",
            "sub-a",
            fullName: "Иван Иванов",
            vacancy: "Слесарь",
            city: "Москва",
            vacancyUrl: "https://www.avito.ru/moskva/vakansii/slesar_8072057107",
            age: 42));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "PerAvitoAccount",
                [],
                [],
                AvitoSubProfileId: "sub-a",
                CardFingerprints: [legacyFingerprint]));

        Assert.NotNull(result);
        Assert.Equal([legacyFingerprint], result!.ExistingCardFingerprints);
    }

    [Fact]
    public async Task LookupAsync_BatchPhones_IgnoresOtherOffices()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        db.CandidateResponses.Add(NewResponse("79005555555", "sub-a", officeId: OtherOfficeId));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "GlobalAcrossAllAccounts",
                [],
                ["79005555555"]));

        Assert.NotNull(result);
        Assert.Empty(result!.ExistingPhones);
    }

    [Fact]
    public async Task LookupAsync_Profiles_SamePhone_ReturnsMatchedIndex()
    {
        await using var db = CreateDb();
        SeedWorker(db);
        var person = TestCandidatePersonFactory.CreatePerson(
            OfficeId,
            fullName: "Игорь мокрушин",
            firstName: "мокрушин",
            lastName: "игорь",
            phoneNormalized: "79339313951");
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            phone: "79339313951",
            fullName: "Игорь мокрушин"));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "GlobalAcrossAllAccounts",
                [],
                [],
                Profiles:
                [
                    new CandidateLookupProfileDto("Игорь мокрушин", 30, "Электросталь", "79339313951")
                ]));

        Assert.NotNull(result);
        Assert.Equal([0], result!.MatchedProfileIndexes);
    }

    [Fact]
    public async Task LookupAsync_Profiles_DifferentPhoneSameAgeCity_ReturnsMatchedIndex()
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
            phoneNormalized: "79930099416");
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            phone: "79930099416",
            fullName: "Гор Олег Александрович",
            age: 66,
            city: "рабочий поселок Чик"));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "GlobalAcrossAllAccounts",
                [],
                [],
                Profiles:
                [
                    new CandidateLookupProfileDto(
                        "Гор Олег Александрович",
                        66,
                        "рабочий поселок Чик",
                        "79910001122")
                ]));

        Assert.NotNull(result);
        Assert.Equal([0], result!.MatchedProfileIndexes);
    }

    [Fact]
    public async Task LookupAsync_Profiles_SameFullNameDifferentAge_IsMatch()
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
            phoneNormalized: "79930099416");
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            phone: "79930099416",
            fullName: "Гор Олег Александрович",
            age: 66,
            city: "рабочий поселок Чик"));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "GlobalAcrossAllAccounts",
                [],
                [],
                Profiles:
                [
                    new CandidateLookupProfileDto(
                        "Гор Олег Александрович",
                        40,
                        "рабочий поселок Чик",
                        "79910001122")
                ]));

        Assert.NotNull(result);
        Assert.Equal([0], result!.MatchedProfileIndexes);
    }

    [Fact]
    public async Task LookupAsync_Profiles_DifferentFullName_ReturnsEmpty()
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
            phoneNormalized: "79930099416");
        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(TestCandidatePersonFactory.CreateResponse(
            OfficeId,
            person.Id,
            phone: "79930099416",
            fullName: "Гор Олег Александрович",
            age: 66,
            city: "рабочий поселок Чик"));
        await db.SaveChangesAsync();

        var sut = CreateSut(db);
        var result = await sut.LookupAsync(
            WorkerId,
            new WorkerCandidateLookupRequest(
                AccountId,
                "GlobalAcrossAllAccounts",
                [],
                [],
                Profiles:
                [
                    new CandidateLookupProfileDto(
                        "Иванов Иван Иванович",
                        66,
                        "рабочий поселок Чик",
                        "79930099416")
                ]));

        Assert.NotNull(result);
        Assert.Empty(result!.MatchedProfileIndexes);
    }

    private static CandidateLookupService CreateSut(OrbitaDbContext db) =>
        new(db, new CandidatePersonMatchService(db));

    private static OrbitaDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrbitaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrbitaDbContext(options);
    }

    private static void SeedWorker(OrbitaDbContext db)
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
        db.SaveChanges();
    }

    private static CandidateResponseEntity NewResponse(
        string phone,
        string subProfileId,
        Guid? accountId = null,
        Guid? officeId = null,
        string? sourceResponseId = null,
        DateTime? createdAt = null,
        string? cardFingerprint = null,
        string? fullName = null,
        string? vacancy = null,
        string? city = null,
        string? vacancyUrl = null,
        int? age = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId ?? OfficeId,
            WorkerId = WorkerId,
            AccountId = accountId ?? AccountId,
            AccountName = "acc",
            Source = "Avito",
            SourceResponseId = sourceResponseId ?? Guid.NewGuid().ToString("N"),
            CardFingerprint = cardFingerprint ?? string.Empty,
            FullName = fullName ?? "User",
            PhoneRaw = phone,
            PhoneNormalized = phone,
            Vacancy = vacancy ?? string.Empty,
            City = city ?? string.Empty,
            VacancyUrl = vacancyUrl ?? string.Empty,
            Age = age,
            AvitoSubProfileId = subProfileId,
            Status = ResponseStatuses.Sent,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            CollectedAt = createdAt ?? DateTime.UtcNow
        };
}
