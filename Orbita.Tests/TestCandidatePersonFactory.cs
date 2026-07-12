using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Tests;

internal static class TestCandidatePersonFactory
{
    public static CandidatePersonEntity CreatePerson(
        Guid officeId,
        string fullName = "Test User",
        string firstName = "User",
        string lastName = "Test",
        string middleName = "",
        int? age = 25,
        string city = "Москва",
        string phoneRaw = "79001111111",
        string phoneNormalized = "79001111111",
        DateTime? createdAtUtc = null)
    {
        var createdAt = createdAtUtc ?? DateTime.UtcNow;
        return new CandidatePersonEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            FullName = fullName,
            FirstName = firstName,
            LastName = lastName,
            MiddleName = middleName,
            Age = age,
            City = city,
            PhoneRaw = phoneRaw,
            PhoneNormalized = phoneNormalized,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt
        };
    }

    public static CandidateResponseEntity CreateResponse(
        Guid officeId,
        Guid personId,
        Guid? workerId = null,
        string phone = "79001111111",
        Guid? id = null,
        string sourceResponseId = "",
        string fullName = "Test User",
        int? age = 25,
        string city = "Москва",
        DateTime? createdAt = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        PersonId = personId,
        OfficeId = officeId,
        WorkerId = workerId ?? Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        AccountName = "acc",
        Source = "Avito",
        SourceResponseId = string.IsNullOrWhiteSpace(sourceResponseId)
            ? Guid.NewGuid().ToString("N")
            : sourceResponseId,
        FullName = fullName,
        FirstName = "User",
        LastName = "Test",
        Age = age,
        City = city,
        PhoneRaw = phone,
        PhoneNormalized = phone,
        Status = ResponseStatuses.Sent,
        CreatedAt = createdAt ?? DateTime.UtcNow
    };
}