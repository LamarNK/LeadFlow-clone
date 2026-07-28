using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;

namespace Orbita.Api.Services;

public sealed class CandidatePersonPhoneService(OrbitaDbContext db)
{
    public async Task ApplyPhoneFromResponseAsync(
        CandidatePersonEntity person,
        string phoneRaw,
        string phoneNormalized,
        Guid? responseId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return;
        }

        var hasHistory = await db.CandidatePhoneHistory
            .AnyAsync(x => x.PersonId == person.Id, ct);

        if (string.IsNullOrWhiteSpace(person.PhoneNormalized))
        {
            person.PhoneRaw = phoneRaw;
            person.PhoneNormalized = phoneNormalized;
            person.UpdatedAtUtc = DateTime.UtcNow;
            AppendHistory(person.Id, phoneRaw, phoneNormalized, responseId);
            await db.SaveChangesAsync(ct);
            return;
        }

        if (string.Equals(person.PhoneNormalized, phoneNormalized, StringComparison.Ordinal))
        {
            if (!hasHistory)
            {
                AppendHistory(person.Id, phoneRaw, phoneNormalized, responseId);
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        if (!hasHistory)
        {
            AppendHistory(person.Id, person.PhoneRaw, person.PhoneNormalized, responseId: null);
        }

        person.PhoneRaw = phoneRaw;
        person.PhoneNormalized = phoneNormalized;
        person.UpdatedAtUtc = DateTime.UtcNow;
        AppendHistory(person.Id, phoneRaw, phoneNormalized, responseId);
        await db.SaveChangesAsync(ct);
    }

    private void AppendHistory(
        Guid personId,
        string phoneRaw,
        string phoneNormalized,
        Guid? responseId)
    {
        db.CandidatePhoneHistory.Add(new CandidatePhoneHistoryEntity
        {
            Id = Guid.NewGuid(),
            PersonId = personId,
            ResponseId = responseId,
            PhoneRaw = phoneRaw,
            PhoneNormalized = phoneNormalized,
            RecordedAtUtc = DateTime.UtcNow
        });
    }
}
