using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CandidatePersonMatchService(OrbitaDbContext db)
{
    public async Task<CandidatePersonEntity?> FindMatchingPersonAsync(
        Guid officeId,
        CandidateMatchProfile incoming,
        CancellationToken ct = default)
    {
        var incomingName = CandidateNameNormalizer.Normalize(incoming.FullName);
        if (incomingName.LastName.Length == 0 || incomingName.FirstName.Length == 0)
        {
            return null;
        }

        var duplicateCutoffUtc = CandidateDuplicateLookback.GetCutoffUtc(DateTime.UtcNow);
        var nameMatches = await db.CandidatePersons
            .AsNoTracking()
            .Where(x => x.OfficeId == officeId
                        && x.LastName.ToLower() == incomingName.LastName
                        && x.FirstName.ToLower() == incomingName.FirstName
                        && x.MiddleName.ToLower() == incomingName.MiddleName)
            .ToListAsync(ct);

        if (nameMatches.Count == 0)
        {
            return null;
        }

        var candidatePersonIds = nameMatches.Select(x => x.Id).ToArray();
        var activePersonIds = await db.CandidateResponses
            .AsNoTracking()
            .Where(x => x.OfficeId == officeId
                        && x.CreatedAt >= duplicateCutoffUtc
                        && candidatePersonIds.Contains(x.PersonId))
            .Select(x => x.PersonId)
            .Distinct()
            .ToListAsync(ct);

        var activeMatches = nameMatches
            .Where(x => activePersonIds.Contains(x.Id))
            .ToList();

        var (best, _) = CandidateMatchScorer.TryFindBestMatch(
            activeMatches,
            incoming,
            static person => new CandidateMatchProfile(
                person.FullName,
                person.Age,
                person.City,
                person.PhoneNormalized));

        return best;
    }

    public CandidatePersonEntity CreatePerson(
        Guid officeId,
        string fullName,
        string firstName,
        string lastName,
        string middleName,
        int? age,
        string city,
        string phoneRaw,
        string phoneNormalized,
        DateTime utcNow)
    {
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
            CreatedAtUtc = utcNow,
            UpdatedAtUtc = utcNow
        };
    }

    public static CandidateMatchProfile ToProfile(
        string fullName,
        int? age,
        string city,
        string phoneNormalized) =>
        new(fullName, age, city, phoneNormalized);

    public static CandidateMatchProfile ToProfile(CandidateResponseEntity entity) =>
        new(entity.FullName, entity.Age, entity.City, entity.PhoneNormalized);

    public static CandidateMatchProfile ToProfile(CandidatePersonEntity entity) =>
        new(entity.FullName, entity.Age, entity.City, entity.PhoneNormalized);
}