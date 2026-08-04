using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CandidateBitrixSendService(
    OrbitaDbContext db,
    BitrixInstanceService bitrixInstances,
    BitrixClient bitrixClient,
    IOptions<OrbitaBitrixSettings> defaultBitrixOptions)
{
    public async Task<(bool Success, string? Error, string? EntityId, string? ContactId)> SendAsync(
        CandidateResponseEntity entity,
        BitrixInstanceEntity instance,
        CancellationToken ct = default)
    {
        var webhookUrl = await bitrixInstances.ResolveWebhookUrlAsync(instance, ct);
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return (false, "Не настроен валидный вебхук Bitrix24.", null, null);
        }

        var settings = BitrixInstanceIntegrationSettings
            .Parse(instance.IntegrationSettingsJson, defaultBitrixOptions.Value)
            .ToOrbitaBitrixSettings();

        entity.BitrixEntityType = settings.EntityType;
        var lead = await MapLeadAsync(entity, ct);
        var result = await bitrixClient.CreateLeadAsync(lead, webhookUrl, settings, ct);
        return result.IsSuccess
            ? (true, null, result.EntityId, result.ContactId)
            : (false, result.Error, result.EntityId, result.ContactId);
    }

    public static string FormatBitrixDisplayName(BitrixInstanceEntity instance) =>
        !string.IsNullOrWhiteSpace(instance.Signature) ? instance.Signature : instance.Name;

    public static string BuildDuplicateSummary(BitrixInstanceEntity instance) =>
        $"Дубль в {FormatBitrixDisplayName(instance)}";

    private async Task<CandidateLead> MapLeadAsync(CandidateResponseEntity entity, CancellationToken ct)
    {
        var history = await LoadPhoneHistoryAsync(entity, ct);
        return new CandidateLead
        {
            AccountId = entity.AccountId,
            AccountName = entity.AccountName,
            Source = entity.Source,
            SourceResponseId = entity.SourceResponseId,
            FullName = entity.FullName,
            FirstName = entity.FirstName,
            LastName = entity.LastName,
            MiddleName = entity.MiddleName,
            Age = entity.Age,
            PhoneRaw = entity.PhoneRaw,
            PhoneNormalized = entity.PhoneNormalized,
            City = entity.City,
            Vacancy = entity.Vacancy,
            VacancyUrl = entity.VacancyUrl,
            MessengerUrl = entity.MessengerUrl,
            AvitoSubProfileId = entity.AvitoSubProfileId,
            RawText = entity.RawText,
            CreatedAt = entity.CreatedAt,
            PhoneHistory = history
        };
    }

    private async Task<IReadOnlyList<CandidateLeadPhoneHistoryItem>> LoadPhoneHistoryAsync(
        CandidateResponseEntity entity,
        CancellationToken ct)
    {
        // Предпочитаем историю, привязанную к отклику; если пусто — по человеку.
        var forResponse = await db.CandidatePhoneHistory.AsNoTracking()
            .Where(x => x.ResponseId == entity.Id)
            .OrderBy(x => x.RecordedAtUtc)
            .Select(x => new CandidateLeadPhoneHistoryItem(x.PhoneRaw, x.PhoneNormalized, x.RecordedAtUtc))
            .ToListAsync(ct);

        if (forResponse.Count > 0)
        {
            return forResponse;
        }

        if (entity.PersonId == Guid.Empty)
        {
            return [];
        }

        return await db.CandidatePhoneHistory.AsNoTracking()
            .Where(x => x.PersonId == entity.PersonId)
            .OrderBy(x => x.RecordedAtUtc)
            .Select(x => new CandidateLeadPhoneHistoryItem(x.PhoneRaw, x.PhoneNormalized, x.RecordedAtUtc))
            .ToListAsync(ct);
    }
}
