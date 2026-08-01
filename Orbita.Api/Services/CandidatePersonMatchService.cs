using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CandidatePersonMatchService(OrbitaDbContext db)
{
    /// <summary>
    /// Global person match for collection pool (no office filter).
    /// Optional <paramref name="officeId"/> narrows match to that office when set.
    /// </summary>
    public async Task<CandidatePersonEntity?> FindMatchingPersonAsync(
        Guid? officeId,
        CandidateMatchProfile incoming,
        CancellationToken ct = default)
    {
        var incomingName = CandidateNameNormalizer.Normalize(incoming.FullName);
        if (!CandidateNameNormalizer.HasAnyNamePart(incomingName))
        {
            return null;
        }

        var isCompleteFio = CandidateNameNormalizer.IsCompleteFio(incomingName);
        var personsQuery = db.CandidatePersons.AsNoTracking()
            .Where(x => x.LastName.ToLower() == incomingName.LastName
                        && x.FirstName.ToLower() == incomingName.FirstName
                        && x.MiddleName.ToLower() == incomingName.MiddleName);
        if (officeId is Guid oid)
        {
            personsQuery = personsQuery.Where(x => x.OfficeId == null || x.OfficeId == oid);
        }

        var nameMatches = await personsQuery.ToListAsync(ct);
        if (nameMatches.Count == 0)
        {
            return null;
        }

        var candidatePersonIds = nameMatches.Select(x => x.Id).ToArray();
        List<Guid> activePersonIds;
        if (isCompleteFio)
        {
            var duplicateCutoffUtc = CandidateDuplicateLookback.GetCutoffUtc(DateTime.UtcNow);
            var responsesQuery = db.CandidateResponses.AsNoTracking()
                .Where(x => x.CreatedAt >= duplicateCutoffUtc && candidatePersonIds.Contains(x.PersonId));
            if (officeId is Guid responseOfficeId)
            {
                responsesQuery = responsesQuery.Where(x => x.OfficeId == null || x.OfficeId == responseOfficeId);
            }

            activePersonIds = await responsesQuery
                .Select(x => x.PersonId)
                .Distinct()
                .ToListAsync(ct);
        }
        else
        {
            var (windowStart, windowEnd) = CandidateDuplicateLookback.GetIncompleteNameWindow(
                incoming.ResponseAtUtc);
            var responsesQuery = db.CandidateResponses.AsNoTracking()
                .Where(x => x.CreatedAt >= windowStart
                            && x.CreatedAt <= windowEnd
                            && candidatePersonIds.Contains(x.PersonId));
            if (officeId is Guid responseOfficeId)
            {
                responsesQuery = responsesQuery.Where(x => x.OfficeId == null || x.OfficeId == responseOfficeId);
            }

            activePersonIds = await responsesQuery
                .Select(x => x.PersonId)
                .Distinct()
                .ToListAsync(ct);
        }

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
        Guid? officeId,
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
        string phoneNormalized,
        DateTime? responseAtUtc = null) =>
        new(fullName, age, city, phoneNormalized, responseAtUtc);

    public static CandidateMatchProfile ToProfile(CandidateResponseEntity entity) =>
        new(entity.FullName, entity.Age, entity.City, entity.PhoneNormalized, entity.CreatedAt);

    public static CandidateMatchProfile ToProfile(CandidatePersonEntity entity) =>
        new(entity.FullName, entity.Age, entity.City, entity.PhoneNormalized);
}
