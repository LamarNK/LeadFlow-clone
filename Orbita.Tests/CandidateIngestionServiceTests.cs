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
    public async Task IngestBatchAsync_LocalDuplicate_StoresDuplicateStatus()
    {
        await using var db = CreateDb();
        SeedWorker(db);

        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = OfficeId,
            WorkerId = WorkerId,
            AccountId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            AccountName = "acc",
            Source = "Avito",
            SourceResponseId = "existing-source",
            FullName = "Existing User",
            PhoneRaw = "+7 (900) 111-11-11",
            PhoneNormalized = "79001111111",
            Status = ResponseStatuses.Sent,
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var sut = CreateService(db);
        var request = new WorkerCandidateBatchRequest([
            new WorkerCandidateDto(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                "acc",
                "Avito",
                "new-source-id",
                "",
                "New User",
                25,
                "+7 (900) 111-11-11",
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

        Assert.Equal(1, result.SkippedDuplicates);
        Assert.Equal(ResponseStatuses.Duplicate, result.Items[0].Status);

        var stored = await db.CandidateResponses.SingleAsync(x => x.SourceResponseId == "new-source-id");
        Assert.Equal(ResponseStatuses.Duplicate, stored.Status);
        Assert.True(stored.IsLocalDuplicate);
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
    }

    private static CandidateIngestionService CreateService(OrbitaDbContext db)
    {
        var bitrixOptions = Options.Create(new OrbitaBitrixSettings { CheckDuplicatesInBitrix = false });
        var duplicateService = new CandidateDuplicateService(db, new BitrixClient(new HttpClientFactoryStub(), new CandidateParser()));
        var audit = new PanelAuditService(db);
        var bitrixInstanceService = new BitrixInstanceService(db, null!, null!, bitrixOptions, audit);
        var distributionRoute = new DistributionRouteService(db, audit);
        var distributionEngine = new DistributionEngine(db);
        var bitrixDuplicateCheck = new BitrixDuplicateCheckAllService(
            bitrixInstanceService,
            new BitrixClient(new HttpClientFactoryStub(), new CandidateParser()));
        var bitrixSend = new CandidateBitrixSendService(bitrixInstanceService, new BitrixClient(new HttpClientFactoryStub(), new CandidateParser()), bitrixOptions);
        var deliveries = new ResponseBitrixDeliveryService(db);
        var autoDistribution = new CandidateAutoDistributionService(bitrixDuplicateCheck, bitrixSend, deliveries);
        var manualSend = new ManualBitrixSendService(db, duplicateService, bitrixDuplicateCheck, bitrixSend, deliveries, new NoopPanelRealtimeNotifier());

        return new CandidateIngestionService(
            db,
            new PhoneNormalizer(),
            new CandidateParser(),
            duplicateService,
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