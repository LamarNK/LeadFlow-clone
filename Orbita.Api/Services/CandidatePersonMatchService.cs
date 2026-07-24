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
        // Хотя бы один токен (в т.ч. «только Иван»). Полное ФИО матчится по имени;
        // неполное — только если scorer наберёт порог за счёт age/city (или phone)
        // и есть отклик в узком временном окне (~неделя).
        if (!CandidateNameNormalizer.HasAnyNamePart(incomingName))
        {
            return null;
        }

        var isCompleteFio = CandidateNameNormalizer.IsCompleteFio(incomingName);
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
        List<Guid> activePersonIds;
        if (isCompleteFio)
        {
            var duplicateCutoffUtc = CandidateDuplicateLookback.GetCutoffUtc(DateTime.UtcNow);
            activePersonIds = await db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.OfficeId == officeId
                            && x.CreatedAt >= duplicateCutoffUtc
                            && candidatePersonIds.Contains(x.PersonId))
                .Select(x => x.PersonId)
                .Distinct()
                .ToListAsync(ct);
        }
        else
        {
            // Неполное имя: только отклики около даты этого отклика (±7 дней).
            var (windowStart, windowEnd) = CandidateDuplicateLookback.GetIncompleteNameWindow(
                incoming.ResponseAtUtc);
            activePersonIds = await db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.OfficeId == officeId
                            && x.CreatedAt >= windowStart
                            && x.CreatedAt <= windowEnd
                            && candidatePersonIds.Contains(x.PersonId))
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
        string phoneNormalized,
        DateTime? responseAtUtc = null) =>
        new(fullName, age, city, phoneNormalized, responseAtUtc);

    public static CandidateMatchProfile ToProfile(CandidateResponseEntity entity) =>
        new(entity.FullName, entity.Age, entity.City, entity.PhoneNormalized, entity.CreatedAt);

    public static CandidateMatchProfile ToProfile(CandidatePersonEntity entity) =>
        new(entity.FullName, entity.Age, entity.City, entity.PhoneNormalized);
}
