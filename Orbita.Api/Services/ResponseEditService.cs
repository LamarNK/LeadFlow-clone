using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class ResponseEditService(
    OrbitaDbContext db,
    PhoneNormalizer phoneNormalizer,
    CandidateParser candidateParser,
    CandidatePersonPhoneService personPhone,
    ResponsesQueryService responsesQuery,
    IPanelRealtimeNotifier panelRealtime)
{
    private const int MinAge = 14;
    private const int MaxAge = 99;

    public async Task<UpdateResponseResultDto> UpdateAsync(
        Guid responseId,
        UpdateResponseRequest request,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var entity = await db.CandidateResponses
            .Include(x => x.Worker)
            .Include(x => x.Person)
            .FirstOrDefaultAsync(x => x.Id == responseId, ct);
        if (entity is null)
        {
            return new UpdateResponseResultDto(false, "Отклик не найден.");
        }

        var sharedWithScope = false;
        if (!scope.IsGlobalAdmin && scope.OfficeId is Guid scopeOfficeId)
        {
            sharedWithScope = await db.ResponseCrmDeliveries.AsNoTracking()
                .AnyAsync(
                    d => d.ResponseId == entity.Id
                         && d.OfficeId == scopeOfficeId
                         && d.Outcome == ResponseCrmDeliveryOutcomes.Sent,
                    ct);
            if (!sharedWithScope)
            {
                sharedWithScope = await db.CrmCandidateCards.AsNoTracking()
                    .AnyAsync(c => c.ResponseId == entity.Id && c.OfficeId == scopeOfficeId, ct);
            }
        }

        if (!scope.CanAccessResponse(entity.OfficeId, entity.Worker?.OfficeId, sharedWithScope))
        {
            return new UpdateResponseResultDto(false, "Нет доступа к отклику.");
        }

        var fullName = (request.FullName ?? string.Empty).Trim();
        if (fullName.Length == 0)
        {
            return new UpdateResponseResultDto(false, "Укажите ФИО кандидата.");
        }

        if (fullName.Length > 256)
        {
            return new UpdateResponseResultDto(false, "ФИО слишком длинное.");
        }

        var phoneRaw = (request.PhoneRaw ?? string.Empty).Trim();
        var phoneNormalized = phoneNormalizer.Normalize(phoneRaw);
        if (string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return new UpdateResponseResultDto(false, "Укажите корректный телефон.");
        }

        var city = (request.City ?? string.Empty).Trim();
        if (city.Length > 256)
        {
            return new UpdateResponseResultDto(false, "Город слишком длинный.");
        }

        int? age = request.Age;
        if (age is int ageValue)
        {
            if (ageValue is < MinAge or > MaxAge)
            {
                return new UpdateResponseResultDto(false, $"Возраст должен быть от {MinAge} до {MaxAge}.");
            }
        }
        else
        {
            age = null;
        }

        var gender = NormalizeGender(request.Gender);

        var (firstName, lastName, middleName) = candidateParser.ParseName(fullName);
        var changed = false;
        var locks = entity.OperatorLockedFields;

        if (!string.Equals(entity.FullName, fullName, StringComparison.Ordinal))
        {
            entity.FullName = fullName;
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.FullName);
            changed = true;
        }

        if (!string.Equals(entity.FirstName, firstName, StringComparison.Ordinal)
            || !string.Equals(entity.LastName, lastName, StringComparison.Ordinal)
            || !string.Equals(entity.MiddleName, middleName, StringComparison.Ordinal))
        {
            entity.FirstName = firstName;
            entity.LastName = lastName;
            entity.MiddleName = middleName;
            changed = true;
        }

        if (entity.Age != age)
        {
            entity.Age = age;
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.Age);
            changed = true;
        }

        if (!string.Equals(entity.Gender ?? string.Empty, gender, StringComparison.Ordinal))
        {
            entity.Gender = gender;
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.Gender);
            changed = true;
        }

        if (!string.Equals(entity.City, city, StringComparison.Ordinal))
        {
            entity.City = city;
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.City);
            changed = true;
        }

        var phoneChanged = !string.Equals(entity.PhoneNormalized, phoneNormalized, StringComparison.Ordinal);
        if (phoneChanged)
        {
            entity.PreviousPhoneRaw = entity.PhoneRaw;
            entity.PreviousPhoneNormalized = entity.PhoneNormalized;
            entity.PhoneMetricKind = ResponsePhoneMetricKinds.PhoneChanged;
            entity.PhoneChangedAtUtc = DateTime.UtcNow;
            entity.PhoneRaw = phoneRaw;
            entity.PhoneNormalized = phoneNormalized;
            changed = true;
        }
        else if (!string.Equals(entity.PhoneRaw, phoneRaw, StringComparison.Ordinal))
        {
            entity.PhoneRaw = phoneRaw;
            changed = true;
        }

        var person = entity.Person;
        if (person is not null)
        {
            if (!string.Equals(person.FullName, fullName, StringComparison.Ordinal)
                || !string.Equals(person.FirstName, firstName, StringComparison.Ordinal)
                || !string.Equals(person.LastName, lastName, StringComparison.Ordinal)
                || !string.Equals(person.MiddleName, middleName, StringComparison.Ordinal)
                || person.Age != age
                || !string.Equals(person.City, city, StringComparison.Ordinal))
            {
                person.FullName = fullName;
                person.FirstName = firstName;
                person.LastName = lastName;
                person.MiddleName = middleName;
                person.Age = age;
                person.City = city;
                person.UpdatedAtUtc = DateTime.UtcNow;
                changed = true;
            }
        }

        if (!string.Equals(entity.OperatorLockedFields, locks, StringComparison.Ordinal))
        {
            entity.OperatorLockedFields = locks;
            changed = true;
        }

        if (phoneChanged && person is not null)
        {
            // Saves response + person changes and appends phone history.
            await personPhone.ApplyPrimaryPhoneFromResponseAsync(
                person,
                phoneRaw,
                phoneNormalized,
                entity.Id,
                ct);
        }
        else if (changed)
        {
            await db.SaveChangesAsync(ct);
        }

        if (!changed)
        {
            var unchanged = await responsesQuery.GetDetailAsync(entity.Id, scope, ct);
            return new UpdateResponseResultDto(true, null, unchanged);
        }

        if (changed)
        {
            var notifyOfficeId = entity.OfficeId ?? entity.Worker?.OfficeId;
            panelRealtime.Notify(
                [PanelChangeKind.Responses, PanelChangeKind.NavBadges],
                notifyOfficeId,
                entity.WorkerId);
        }

        var detail = await responsesQuery.GetDetailAsync(entity.Id, scope, ct);
        return new UpdateResponseResultDto(true, null, detail);
    }

    private static string NormalizeGender(string? gender)
    {
        var value = CandidateGenders.NormalizeFilterValue(gender);
        return value is CandidateGenders.Male or CandidateGenders.Female
            ? value
            : string.Empty;
    }
}
