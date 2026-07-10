using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CandidateBitrixSendService(
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
        var lead = MapLead(entity);
        var result = await bitrixClient.CreateLeadAsync(lead, webhookUrl, settings, ct);
        return result.IsSuccess
            ? (true, null, result.EntityId, result.ContactId)
            : (false, result.Error, result.EntityId, result.ContactId);
    }

    public static string FormatBitrixDisplayName(BitrixInstanceEntity instance) =>
        !string.IsNullOrWhiteSpace(instance.Signature) ? instance.Signature : instance.Name;

    public static string BuildDuplicateSummary(BitrixInstanceEntity instance) =>
        $"Дубль в {FormatBitrixDisplayName(instance)}";

    private static CandidateLead MapLead(CandidateResponseEntity entity) => new()
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
        CreatedAt = entity.CreatedAt
    };
}