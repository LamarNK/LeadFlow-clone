using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class ResponseDeliveryServiceTests
{
    [Fact]
    public async Task Deliver_CrmOnly_InvalidatesResponseCacheBeforeReturning()
    {
        await using var provider = await CreateProviderAsync();
        var db = provider.GetRequiredService<OrbitaDbContext>();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        db.Offices.Add(new OfficeEntity
        {
            Id = officeId, Name = "Cache Office", RegistrationSecretHash = "h",
            CreatedAtUtc = DateTime.UtcNow, IsEnabled = true, CrmEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId, OfficeId = officeId, DisplayName = "W", ApiKeyHash = "h",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.CandidatePersons.Add(new CandidatePersonEntity
        {
            Id = personId, FullName = "Иван", FirstName = "Иван", LastName = "Иванов",
            MiddleName = "", PhoneNormalized = "79000000001",
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        });
        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = responseId, PersonId = personId, WorkerId = workerId,
            AccountId = Guid.NewGuid(), AccountName = "acc", Source = "Avito",
            SourceResponseId = "cache-response", FullName = "Иван",
            PhoneNormalized = "79000000001", Status = ResponseStatuses.ActionRequired,
            CreatedAt = DateTime.UtcNow, CollectedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await provider.GetRequiredService<ResponseDeliveryService>().DeliverAsync(
            responseId,
            new DeliverResponseRequest(officeId, ToCrm: true, ToBitrix: false),
            OfficeScope.ForOffice(officeId));

        Assert.True(result.Success, result.ErrorMessage);
        var cache = provider.GetRequiredService<IOrbitaQueryCache>();
        Assert.Contains(officeId, ((NoopQueryCache)cache).InvalidatedOfficeIds);
    }

    [Fact]
    public async Task Deliver_CrmOnly_CreatesCardAndBindsOffice()
    {
        await using var provider = await CreateProviderAsync();
        var db = provider.GetRequiredService<OrbitaDbContext>();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var personId = Guid.NewGuid();

        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "CRM Office",
            RegistrationSecretHash = "h",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true,
            CrmEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            DisplayName = "W",
            ApiKeyHash = "h",
            CreatedAtUtc = DateTime.UtcNow,
            AutoDeliverToCrm = false,
            AutoDeliverToBitrix = false
        });
        db.CandidatePersons.Add(new CandidatePersonEntity
        {
            Id = personId,
            OfficeId = null,
            FullName = "Иван Иванов",
            FirstName = "Иван",
            LastName = "Иванов",
            MiddleName = "",
            PhoneNormalized = "79001112233",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = responseId,
            PersonId = personId,
            OfficeId = null,
            WorkerId = workerId,
            AccountId = Guid.NewGuid(),
            AccountName = "acc",
            Source = "Avito",
            SourceResponseId = "src-1",
            FullName = "Иван Иванов",
            PhoneRaw = "+7 900 111-22-33",
            PhoneNormalized = "79001112233",
            Status = ResponseStatuses.ActionRequired,
            CreatedAt = DateTime.UtcNow,
            CollectedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var delivery = provider.GetRequiredService<ResponseDeliveryService>();
        var result = await delivery.DeliverAsync(
            responseId,
            new DeliverResponseRequest(officeId, ToCrm: true, ToBitrix: false),
            OfficeScope.ForOffice(officeId));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(ResponseStatuses.Sent, result.Status);
        Assert.Equal(officeId, result.OfficeId);
        Assert.Contains(result.Channels, c => c.Channel == "CRM" && c.Success);

        Assert.True(await db.CrmCandidateCards.AnyAsync(x => x.ResponseId == responseId && x.OfficeId == officeId));
        Assert.Equal(officeId, (await db.CandidateResponses.SingleAsync(x => x.Id == responseId)).OfficeId);
        Assert.True(await db.ResponseCrmDeliveries.AnyAsync(x =>
            x.ResponseId == responseId && x.Outcome == ResponseCrmDeliveryOutcomes.Sent));
    }

    [Fact]
    public async Task Deliver_CrmOnly_DoesNotTransferResponseAlreadyBoundToOffice()
    {
        await using var provider = await CreateProviderAsync();
        var db = provider.GetRequiredService<OrbitaDbContext>();
        var officeA = Guid.NewGuid();
        var officeB = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var personId = Guid.NewGuid();

        foreach (var (id, name) in new[] { (officeA, "Office A"), (officeB, "Office B") })
        {
            db.Offices.Add(new OfficeEntity
            {
                Id = id,
                Name = name,
                RegistrationSecretHash = "h",
                CreatedAtUtc = DateTime.UtcNow,
                IsEnabled = true,
                CrmEnabled = true
            });
        }

        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeA,
            DisplayName = "W",
            ApiKeyHash = "h",
            CreatedAtUtc = DateTime.UtcNow,
            AutoDeliverToCrm = false,
            AutoDeliverToBitrix = false
        });
        db.CandidatePersons.Add(new CandidatePersonEntity
        {
            Id = personId,
            OfficeId = officeA,
            FullName = "Иван Иванов",
            FirstName = "Иван",
            LastName = "Иванов",
            MiddleName = "",
            PhoneNormalized = "79001112233",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = responseId,
            PersonId = personId,
            OfficeId = officeA,
            WorkerId = workerId,
            AccountId = Guid.NewGuid(),
            AccountName = "acc",
            Source = "Avito",
            SourceResponseId = "src-no-transfer",
            FullName = "Иван Иванов",
            PhoneRaw = "+7 900 111-22-33",
            PhoneNormalized = "79001112233",
            Status = ResponseStatuses.ActionRequired,
            CreatedAt = DateTime.UtcNow,
            CollectedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var delivery = provider.GetRequiredService<ResponseDeliveryService>();
        // Operator selects office B; ownership must stay on A (no transfer, no clone).
        var result = await delivery.DeliverAsync(
            responseId,
            new DeliverResponseRequest(officeB, ToCrm: true, ToBitrix: false),
            OfficeScope.GlobalAdmin);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, await db.CandidateResponses.CountAsync());
        Assert.Equal(officeA, (await db.CandidateResponses.SingleAsync(x => x.Id == responseId)).OfficeId);
        Assert.Equal(officeA, (await db.CandidatePersons.SingleAsync(x => x.Id == personId)).OfficeId);
        Assert.True(await db.ResponseCrmDeliveries.AnyAsync(x =>
            x.ResponseId == responseId
            && x.OfficeId == officeB
            && x.Outcome == ResponseCrmDeliveryOutcomes.Sent));

        var notifications = provider.GetRequiredService<CapturingPanelRealtimeNotifier>().Notifications;
        Assert.Contains(notifications, notification =>
            notification.OfficeId == officeA
            && notification.Kinds.Contains(PanelChangeKind.Responses));
        Assert.Contains(notifications, notification =>
            notification.OfficeId == officeB
            && notification.Kinds.Contains(PanelChangeKind.Responses));
    }

    [Fact]
    public async Task Deliver_CrmOnly_BindsWorkerHomeOffice_NotSelectedCrmOffice()
    {
        await using var provider = await CreateProviderAsync();
        var db = provider.GetRequiredService<OrbitaDbContext>();
        var workerOffice = Guid.NewGuid();
        var selectedCrmOffice = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var personId = Guid.NewGuid();

        foreach (var (id, name) in new[] { (workerOffice, "Worker home"), (selectedCrmOffice, "CRM target") })
        {
            db.Offices.Add(new OfficeEntity
            {
                Id = id,
                Name = name,
                RegistrationSecretHash = "h",
                CreatedAtUtc = DateTime.UtcNow,
                IsEnabled = true,
                CrmEnabled = true
            });
        }

        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = workerOffice,
            DisplayName = "W",
            ApiKeyHash = "h",
            CreatedAtUtc = DateTime.UtcNow,
            AutoDeliverToCrm = false,
            AutoDeliverToBitrix = false
        });
        db.CandidatePersons.Add(new CandidatePersonEntity
        {
            Id = personId,
            OfficeId = null,
            FullName = "Пётр Петров",
            FirstName = "Пётр",
            LastName = "Петров",
            MiddleName = "",
            PhoneNormalized = "79002223344",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = responseId,
            PersonId = personId,
            OfficeId = null,
            WorkerId = workerId,
            AccountId = Guid.NewGuid(),
            AccountName = "acc",
            Source = "Avito",
            SourceResponseId = "src-home-bind",
            FullName = "Пётр Петров",
            PhoneRaw = "+7 900 222-33-44",
            PhoneNormalized = "79002223344",
            Status = ResponseStatuses.ActionRequired,
            CreatedAt = DateTime.UtcNow,
            CollectedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var delivery = provider.GetRequiredService<ResponseDeliveryService>();
        var result = await delivery.DeliverAsync(
            responseId,
            new DeliverResponseRequest(selectedCrmOffice, ToCrm: true, ToBitrix: false),
            OfficeScope.GlobalAdmin);

        Assert.True(result.Success, result.ErrorMessage);
        var stored = await db.CandidateResponses
            .Include(x => x.Worker)
            .SingleAsync(x => x.Id == responseId);
        Assert.Equal(workerOffice, stored.OfficeId);
        Assert.Equal(workerOffice, (await db.CandidatePersons.SingleAsync(x => x.Id == personId)).OfficeId);
        Assert.True(await db.ResponseCrmDeliveries.AnyAsync(x =>
            x.ResponseId == responseId && x.OfficeId == selectedCrmOffice));
    }

    [Fact]
    public async Task ApplyAutoDelivery_NoFlags_ActionRequired()
    {
        await using var provider = await CreateProviderAsync();
        var db = provider.GetRequiredService<OrbitaDbContext>();
        var officeId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var responseId = Guid.NewGuid();
        var personId = Guid.NewGuid();

        db.Offices.Add(new OfficeEntity
        {
            Id = officeId,
            Name = "O",
            RegistrationSecretHash = "h",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        });
        var worker = new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            DisplayName = "W",
            ApiKeyHash = "h",
            CreatedAtUtc = DateTime.UtcNow,
            AutoDeliverToCrm = false,
            AutoDeliverToBitrix = false
        };
        db.Workers.Add(worker);
        db.CandidatePersons.Add(new CandidatePersonEntity
        {
            Id = personId,
            FullName = "Пётр",
            FirstName = "Пётр",
            LastName = "",
            MiddleName = "",
            PhoneNormalized = "79000000000",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        var entity = new CandidateResponseEntity
        {
            Id = responseId,
            PersonId = personId,
            WorkerId = workerId,
            AccountId = Guid.NewGuid(),
            AccountName = "a",
            Source = "Avito",
            SourceResponseId = "s2",
            FullName = "Пётр",
            PhoneNormalized = "79000000000",
            Status = ResponseStatuses.InProgress,
            CreatedAt = DateTime.UtcNow,
            CollectedAt = DateTime.UtcNow
        };
        db.CandidateResponses.Add(entity);
        await db.SaveChangesAsync();

        var delivery = provider.GetRequiredService<ResponseDeliveryService>();
        await delivery.ApplyAutoDeliveryAsync(entity, worker);
        await db.Entry(entity).ReloadAsync();
        Assert.Equal(ResponseStatuses.ActionRequired, entity.Status);
    }

    private static async Task<ServiceProvider> CreateProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OrbitaDbContext>(o =>
            o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddIdentityCore<IdentityUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<OrbitaDbContext>();
        services.AddScoped<CrmLeadDistributionService>();
        services.AddScoped<CrmWorkspaceService>();
        services.AddScoped<DistributionEngine>();
        services.AddScoped<CandidatePersonMatchService>();
        services.AddScoped<CandidateDuplicateService>();
        services.AddSingleton<IHttpClientFactory>(_ => new HttpClientFactoryStub());
        services.AddSingleton<CandidateParser>();
        services.AddSingleton(Options.Create(new OrbitaBitrixSettings()));
        services.AddScoped<BitrixClient>();
        services.AddScoped<PanelAuditService>();
        services.AddScoped<ResponseBitrixDeliveryService>();
        services.AddScoped<LeadExportQuotaService>();
        services.AddScoped(sp => new BitrixInstanceService(
            sp.GetRequiredService<OrbitaDbContext>(),
            null!,
            null!,
            Options.Create(new OrbitaBitrixSettings()),
            sp.GetRequiredService<PanelAuditService>()));
        services.AddScoped<BitrixDuplicateCheckAllService>();
        services.AddScoped<CandidateBitrixSendService>();
        services.AddScoped<CandidateAutoDistributionService>();
        services.AddScoped<ResponseCacheInvalidator>();
        services.AddSingleton<IOrbitaQueryCache, NoopQueryCache>();
        services.AddScoped<ManualBitrixSendService>();
        services.AddSingleton<CapturingPanelRealtimeNotifier>();
        services.AddSingleton<IPanelRealtimeNotifier>(provider =>
            provider.GetRequiredService<CapturingPanelRealtimeNotifier>());
        services.AddScoped<ResponseDeliveryService>();

        var provider = services.BuildServiceProvider();
        var roles = provider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(PanelRoles.Manager))
        {
            await roles.CreateAsync(new IdentityRole(PanelRoles.Manager));
        }

        return provider;
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NoopQueryCache : IOrbitaQueryCache
    {
        public List<Guid?> InvalidatedOfficeIds { get; } = [];

        public Task<T> GetOrCreateAsync<T>(OrbitaCacheDomain domain, Guid? officeId, string? audience,
            object? parameters, OrbitaCachePolicy policy, Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default) => factory(cancellationToken);

        public Task<T> GetOrCreateDistributedAsync<T>(OrbitaCacheDomain domain, Guid? officeId, string? audience,
            object? parameters, OrbitaCachePolicy policy, Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default) => factory(cancellationToken);

        public Task InvalidateAsync(IReadOnlyList<PanelChangeKind> changes, Guid? officeId)
        {
            InvalidatedOfficeIds.Add(officeId);
            return Task.CompletedTask;
        }
        public void ClearLocalVersion(OrbitaCacheDomain domain, Guid? officeId) { }
    }
}
