using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class ResponseEditServiceTests
{
    [Fact]
    public async Task UpdateAsync_UpdatesCandidateFields_AndPerson()
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
            Name = "Office",
            RegistrationSecretHash = "h",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            DisplayName = "Worker",
            ApiKeyHash = "h",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.CandidatePersons.Add(new CandidatePersonEntity
        {
            Id = personId,
            OfficeId = null,
            FullName = "Иванов Иван",
            FirstName = "Иван",
            LastName = "Иванов",
            MiddleName = "",
            PhoneRaw = "+7 900 111-22-33",
            PhoneNormalized = "79001112233",
            City = "Москва",
            Age = 30,
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
            SourceResponseId = "src-edit-1",
            FullName = "Иванов Иван",
            FirstName = "Иван",
            LastName = "Иванов",
            PhoneRaw = "+7 900 111-22-33",
            PhoneNormalized = "79001112233",
            City = "Москва",
            Age = 30,
            Gender = CandidateGenders.Male,
            Status = ResponseStatuses.ActionRequired,
            CreatedAt = DateTime.UtcNow,
            CollectedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var edit = provider.GetRequiredService<ResponseEditService>();
        var result = await edit.UpdateAsync(
            responseId,
            new UpdateResponseRequest(
                "Петров Пётр Петрович",
                "8 (901) 222-33-44",
                "Казань",
                28,
                CandidateGenders.Female),
            OfficeScope.ForOffice(officeId));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Detail);

        var response = await db.CandidateResponses.SingleAsync(x => x.Id == responseId);
        Assert.Equal("Петров Пётр Петрович", response.FullName);
        Assert.Equal("Пётр", response.FirstName);
        Assert.Equal("Петров", response.LastName);
        Assert.Equal("Петрович", response.MiddleName);
        Assert.Equal("8 (901) 222-33-44", response.PhoneRaw);
        Assert.Equal("79012223344", response.PhoneNormalized);
        Assert.Equal("Казань", response.City);
        Assert.Equal(28, response.Age);
        Assert.Equal(CandidateGenders.Female, response.Gender);
        Assert.Equal("src-edit-1", response.SourceResponseId);
        Assert.Equal(ResponsePhoneMetricKinds.PhoneChanged, response.PhoneMetricKind);
        Assert.Equal("79001112233", response.PreviousPhoneNormalized);

        var person = await db.CandidatePersons.SingleAsync(x => x.Id == personId);
        Assert.Equal("Петров Пётр Петрович", person.FullName);
        Assert.Equal("79012223344", person.PhoneNormalized);
        Assert.Equal("Казань", person.City);
        Assert.Equal(28, person.Age);

        Assert.True(await db.CandidatePhoneHistory.AnyAsync(x =>
            x.PersonId == personId && x.PhoneNormalized == "79012223344"));
        Assert.True(ResponseOperatorLocks.Contains(response.OperatorLockedFields, ResponseOperatorLocks.FullName));
        Assert.True(ResponseOperatorLocks.Contains(response.OperatorLockedFields, ResponseOperatorLocks.City));
        Assert.True(ResponseOperatorLocks.Contains(response.OperatorLockedFields, ResponseOperatorLocks.Age));
        Assert.True(ResponseOperatorLocks.Contains(response.OperatorLockedFields, ResponseOperatorLocks.Gender));
    }

    [Fact]
    public async Task UpdateAsync_RejectsEmptyPhone()
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
            Name = "Office",
            RegistrationSecretHash = "h",
            CreatedAtUtc = DateTime.UtcNow,
            IsEnabled = true
        });
        db.Workers.Add(new WorkerEntity
        {
            Id = workerId,
            OfficeId = officeId,
            DisplayName = "Worker",
            ApiKeyHash = "h",
            CreatedAtUtc = DateTime.UtcNow
        });
        db.CandidatePersons.Add(new CandidatePersonEntity
        {
            Id = personId,
            FullName = "A B",
            PhoneNormalized = "79001112233",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        db.CandidateResponses.Add(new CandidateResponseEntity
        {
            Id = responseId,
            PersonId = personId,
            WorkerId = workerId,
            AccountId = Guid.NewGuid(),
            Source = "Avito",
            SourceResponseId = "src-2",
            FullName = "A B",
            PhoneRaw = "+79001112233",
            PhoneNormalized = "79001112233",
            Status = ResponseStatuses.ActionRequired,
            CreatedAt = DateTime.UtcNow,
            CollectedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var edit = provider.GetRequiredService<ResponseEditService>();
        var result = await edit.UpdateAsync(
            responseId,
            new UpdateResponseRequest("A B", "abc", "Москва", null, null),
            OfficeScope.ForOffice(officeId));

        Assert.False(result.Success);
        Assert.Contains("телефон", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ServiceProvider> CreateProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OrbitaDbContext>(options =>
            options.UseInMemoryDatabase("response-edit-" + Guid.NewGuid().ToString("N")));
        services.AddSingleton<IPanelRealtimeNotifier, NoopPanelRealtimeNotifier>();
        services.AddSingleton<PhoneNormalizer>();
        services.AddSingleton<CandidateParser>();
        services.AddScoped<CandidatePersonPhoneService>();
        services.AddScoped<ResponseBitrixDeliveryService>();
        services.AddScoped<ResponsesQueryService>();
        services.AddScoped<ResponseEditService>();

        var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<OrbitaDbContext>();
        await db.Database.EnsureCreatedAsync();
        return provider;
    }
}
