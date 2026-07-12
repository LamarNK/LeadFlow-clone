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

        if (string.IsNullOrWhiteSpace(person.PhoneNormalized))
        {
            person.PhoneRaw = phoneRaw;
            person.PhoneNormalized = phoneNormalized;
            person.UpdatedAtUtc = DateTime.UtcNow;
            await AppendHistoryAsync(person.Id, phoneRaw, phoneNormalized, responseId, ct);
            return;
        }

        if (string.Equals(person.PhoneNormalized, phoneNormalized, StringComparison.Ordinal))
        {
            return;
        }

        person.PhoneRaw = phoneRaw;
        person.PhoneNormalized = phoneNormalized;
        person.UpdatedAtUtc = DateTime.UtcNow;
        await AppendHistoryAsync(person.Id, phoneRaw, phoneNormalized, responseId, ct);
    }

    private async Task AppendHistoryAsync(
        Guid personId,
        string phoneRaw,
        string phoneNormalized,
        Guid? responseId,
        CancellationToken ct)
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
        await db.SaveChangesAsync(ct);
    }
}