using Orbita.Api.Data;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class BitrixDuplicateCheckAllService(
    BitrixInstanceService bitrixInstances,
    BitrixClient bitrixClient)
{
    public async Task<(bool IsDuplicate, BitrixInstanceEntity? DuplicateIn, string? UnavailableReason)> CheckAllEnabledAsync(
        Guid officeId,
        CandidateMatchProfile profile,
        CancellationToken ct = default)
    {
        var instances = await bitrixInstances.GetEnabledForOfficeAsync(officeId, ct);
        if (instances.Count == 0)
        {
            return (false, null, null);
        }

        var unavailableReasons = new List<string>();
        var checks = instances.Select(async instance =>
        {
            if (!BitrixValidationStatuses.AllowsWebhookUsage(instance.ValidationStatus))
            {
                return (Instance: instance, IsDuplicate: false, Unavailable: $"Битрикс «{instance.Name}» не прошёл валидацию вебхука.");
            }

            var webhookUrl = await bitrixInstances.ResolveWebhookUrlAsync(instance, ct);
            if (string.IsNullOrWhiteSpace(webhookUrl))
            {
                return (Instance: instance, IsDuplicate: false, Unavailable: $"Битрикс «{instance.Name}»: не настроен валидный вебхук.");
            }

            var lookup = await bitrixClient.HasDuplicateAsync(profile, webhookUrl, ct);
            if (lookup.IsUnavailable)
            {
                return (Instance: instance, IsDuplicate: false, Unavailable: lookup.ErrorMessage ?? $"Битрикс «{instance.Name}» недоступен для проверки дублей.");
            }

            return (Instance: instance, IsDuplicate: lookup.IsDuplicate, Unavailable: (string?)null);
        });

        var results = await Task.WhenAll(checks);
        foreach (var result in results)
        {
            if (result.IsDuplicate)
            {
                return (true, result.Instance, null);
            }

            if (result.Unavailable is not null)
            {
                unavailableReasons.Add(result.Unavailable);
            }
        }

        if (unavailableReasons.Count > 0)
        {
            return (false, null, string.Join(" ", unavailableReasons.Distinct()));
        }

        return (false, null, null);
    }

    public async Task<(bool IsDuplicate, string? UnavailableReason)> CheckInInstanceAsync(
        BitrixInstanceEntity instance,
        CandidateMatchProfile profile,
        CancellationToken ct = default)
    {
        if (!BitrixValidationStatuses.AllowsWebhookUsage(instance.ValidationStatus))
        {
            return (false, "Вебхук Битрикса не прошёл валидацию.");
        }

        var webhookUrl = await bitrixInstances.ResolveWebhookUrlAsync(instance, ct);
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return (false, "Не настроен валидный вебхук Bitrix24.");
        }

        var lookup = await bitrixClient.HasDuplicateAsync(profile, webhookUrl, ct);
        if (lookup.IsUnavailable)
        {
            return (false, lookup.ErrorMessage);
        }

        return (lookup.IsDuplicate, null);
    }
}