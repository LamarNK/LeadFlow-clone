using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;

namespace Orbita.Api.Services;

public sealed class CandidatePersonPhoneService(OrbitaDbContext db)
{
    /// <summary>
    /// An incoming number belongs to the person, not to the office that collected it.
    /// Keep manually selected primary numbers intact; expose new numbers as dialable
    /// contacts on every existing card for that person, including transferred cards.
    /// Caller owns SaveChanges and notifies the returned current CRM offices afterwards.
    /// </summary>
    public async Task<Guid[]> StageCrmContactAsync(
        Guid personId, string phoneRaw, string phoneNormalized, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNormalized)) return [];
        var cards = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => x.Response.PersonId == personId)
            .Select(x => new { x.OfficeId, x.Response.PhoneRaw, x.Response.PhoneNormalized })
            .ToListAsync(ct);
        if (cards.Count == 0) return [];
        var existing = await db.CandidateContactPhones.Where(x => x.PersonId == personId).ToListAsync(ct);
        var known = existing.Select(x => x.PhoneNormalized)
            .Concat(db.CandidateContactPhones.Local.Where(x => x.PersonId == personId).Select(x => x.PhoneNormalized))
            .ToHashSet(StringComparer.Ordinal);
        var hasPrimary = existing.Any(x => x.IsPrimary)
            || db.CandidateContactPhones.Local.Any(x => x.PersonId == personId && x.IsPrimary);
        void Add(string raw, string normalized, bool primary)
        {
            if (string.IsNullOrWhiteSpace(normalized) || !known.Add(normalized)) return;
            db.CandidateContactPhones.Add(new CandidateContactPhoneEntity
            {
                Id = Guid.NewGuid(), PersonId = personId, PhoneRaw = raw, PhoneNormalized = normalized,
                IsPrimary = primary, Label = primary ? null : "Номер из нового отклика",
                CreatedAtUtc = DateTime.UtcNow, CreatedByUserId = "system"
            });
            hasPrimary |= primary;
        }
        foreach (var card in cards) Add(card.PhoneRaw, card.PhoneNormalized, !hasPrimary);
        Add(phoneRaw, phoneNormalized, !hasPrimary);
        return cards.Select(x => x.OfficeId).Distinct().ToArray();
    }

    /// <param name="save">
    /// When false, stages person + history only — caller owns the unit of work.
    /// </param>
    public async Task ApplyPhoneFromResponseAsync(
        CandidatePersonEntity person,
        string phoneRaw,
        string phoneNormalized,
        Guid? responseId,
        CancellationToken ct = default,
        bool save = true)
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
            if (save)
            {
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        if (string.Equals(person.PhoneNormalized, phoneNormalized, StringComparison.Ordinal))
        {
            if (!hasHistory)
            {
                AppendHistory(person.Id, phoneRaw, phoneNormalized, responseId);
                if (save)
                {
                    await db.SaveChangesAsync(ct);
                }
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
        if (save)
        {
            await db.SaveChangesAsync(ct);
        }
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
