using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;
using CandidateParser = Orbita.Api.Services.CandidateParser;

namespace Orbita.Tests;

public sealed class CandidateIngestionServiceTests
{
    private static readonly Guid OfficeId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

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
    public async Task IngestBatchAsync_PhoneUnchangedMetric_StoresInformationalDuplicate()
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
        await db.SaveChangesAsync();

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

        Assert.Equal(1, result.SkippedDuplicates);
        Assert.Equal(ResponseStatuses.Duplicate, result.Items[0].Status);

        var stored = await db.CandidateResponses.SingleAsync(x => x.SourceResponseId == "phone-stable:abc");
        Assert.Equal(ResponsePhoneMetricKinds.PhoneUnchanged, stored.PhoneMetricKind);
        Assert.Equal(48, stored.PhoneUnchangedHours);
        Assert.True(stored.IsLocalDuplicate);
        Assert.Contains("не менялся", stored.DuplicateSummary, StringComparison.OrdinalIgnoreCase);
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
            new BitrixClient(new HttpClientFactoryStub(), new CandidateParser()));
        var bitrixSend = new CandidateBitrixSendService(bitrixInstanceService, new BitrixClient(new HttpClientFactoryStub(), new CandidateParser()), bitrixOptions);
        var deliveries = new ResponseBitrixDeliveryService(db);
        var leadExportQuota = new LeadExportQuotaService(db);
        var autoDistribution = new CandidateAutoDistributionService(bitrixDuplicateCheck, bitrixSend, deliveries, leadExportQuota);
        var manualSend = new ManualBitrixSendService(db, duplicateService, bitrixDuplicateCheck, bitrixSend, deliveries, new NoopPanelRealtimeNotifier());

        return new CandidateIngestionService(
            db,
            new PhoneNormalizer(),
            new CandidateParser(),
            personMatch,
            personPhone,
            distributionRoute,
            distributionEngine,
            autoDistribution,
            manualSend,
            bitrixOptions,
            new NoopPanelRealtimeNotifier());
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
            CreatedAtUtc = DateTime.UtcNow
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
