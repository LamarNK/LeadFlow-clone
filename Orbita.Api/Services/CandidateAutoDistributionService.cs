using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CandidateAutoDistributionService(
    BitrixDuplicateCheckAllService duplicateCheck,
    CandidateBitrixSendService bitrixSend,
    ResponseBitrixDeliveryService deliveries)
{
    public async Task<AutoDistributionResult> DistributeAsync(
        CandidateResponseEntity entity,
        DistributionPlan plan,
        CancellationToken ct = default)
    {
        if (plan.Error is not null || plan.Targets.Count == 0)
        {
            return AutoDistributionResult.ActionRequired(plan.Error ?? "Автоматическое распределение недоступно.");
        }

        return plan.Topology switch
        {
            DistributionTopology.Broadcast => await DistributeBroadcastAsync(entity, plan.Targets, ct),
            DistributionTopology.ChainFallback => await DistributeChainFallbackAsync(entity, plan.Targets, ct),
            _ => AutoDistributionResult.ActionRequired(
                plan.Error ?? "Схема связей должна быть либо отдельными узлами, либо одной цепочкой.")
        };
    }

    private async Task<AutoDistributionResult> DistributeBroadcastAsync(
        CandidateResponseEntity entity,
        IReadOnlyList<BitrixInstanceEntity> targets,
        CancellationToken ct)
    {
        var sent = new List<string>();
        var duplicates = new List<string>();
        var failures = new List<string>();
        BitrixInstanceEntity? firstSuccess = null;
        string? firstEntityId = null;
        string? firstContactId = null;

        foreach (var instance in targets)
        {
            var attempt = await TrySendToInstanceAsync(entity, instance, DistributionModes.Broadcast, ct);
            switch (attempt.Outcome)
            {
                case SendAttemptOutcome.Sent:
                    sent.Add(CandidateBitrixSendService.FormatBitrixDisplayName(instance));
                    firstSuccess ??= instance;
                    firstEntityId ??= attempt.EntityId;
                    firstContactId ??= attempt.ContactId;
                    break;
                case SendAttemptOutcome.Duplicate:
                    duplicates.Add(CandidateBitrixSendService.FormatBitrixDisplayName(instance));
                    break;
                default:
                    failures.Add($"{CandidateBitrixSendService.FormatBitrixDisplayName(instance)}: {attempt.Error}");
                    break;
            }
        }

        if (sent.Count > 0)
        {
            return AutoDistributionResult.Sent(
                firstSuccess!,
                firstEntityId ?? string.Empty,
                firstContactId ?? string.Empty,
                DistributionModes.Broadcast,
                BuildBroadcastSummary(sent, duplicates, failures));
        }

        if (duplicates.Count == targets.Count)
        {
            var duplicateInstance = targets[^1];
            return AutoDistributionResult.Duplicate(
                duplicateInstance,
                $"Дубль во всех Битриксах: {string.Join(", ", duplicates)}");
        }

        if (duplicates.Count > 0 && failures.Count > 0)
        {
            return AutoDistributionResult.ActionRequired(
                $"Дубль: {string.Join(", ", duplicates)}. Ошибки: {string.Join("; ", failures)}");
        }

        if (duplicates.Count > 0)
        {
            return AutoDistributionResult.Duplicate(
                targets[^1],
                $"Дубль: {string.Join(", ", duplicates)}");
        }

        return AutoDistributionResult.Error(string.Join("; ", failures));
    }

    private async Task<AutoDistributionResult> DistributeChainFallbackAsync(
        CandidateResponseEntity entity,
        IReadOnlyList<BitrixInstanceEntity> targets,
        CancellationToken ct)
    {
        var duplicates = new List<string>();
        var failures = new List<string>();

        foreach (var instance in targets)
        {
            var attempt = await TrySendToInstanceAsync(entity, instance, DistributionModes.ChainFallback, ct);
            switch (attempt.Outcome)
            {
                case SendAttemptOutcome.Sent:
                    return AutoDistributionResult.Sent(
                        instance,
                        attempt.EntityId ?? string.Empty,
                        attempt.ContactId ?? string.Empty,
                        DistributionModes.ChainFallback,
                        null);
                case SendAttemptOutcome.Duplicate:
                    duplicates.Add(CandidateBitrixSendService.FormatBitrixDisplayName(instance));
                    break;
                case SendAttemptOutcome.Unavailable:
                    failures.Add($"{CandidateBitrixSendService.FormatBitrixDisplayName(instance)}: {attempt.Error}");
                    break;
                default:
                    failures.Add($"{CandidateBitrixSendService.FormatBitrixDisplayName(instance)}: {attempt.Error}");
                    break;
            }
        }

        if (duplicates.Count == targets.Count)
        {
            return AutoDistributionResult.Duplicate(
                targets[^1],
                $"Дубль по всей цепочке: {string.Join(" → ", duplicates)}");
        }

        if (duplicates.Count > 0 && failures.Count > 0)
        {
            return AutoDistributionResult.ActionRequired(
                $"Дубль: {string.Join(", ", duplicates)}. Ошибки: {string.Join("; ", failures)}");
        }

        if (duplicates.Count > 0)
        {
            return AutoDistributionResult.Duplicate(
                targets[^1],
                $"Дубль: {string.Join(", ", duplicates)}");
        }

        return failures.Count > 0
            ? AutoDistributionResult.ActionRequired(string.Join("; ", failures))
            : AutoDistributionResult.Error("Не удалось отправить отклик по цепочке.");
    }

    private async Task<SendAttemptResult> TrySendToInstanceAsync(
        CandidateResponseEntity entity,
        BitrixInstanceEntity instance,
        string source,
        CancellationToken ct)
    {
        var (isDuplicate, unavailableReason) = await duplicateCheck.CheckInInstanceAsync(
            instance,
            entity.PhoneNormalized,
            ct);
        if (unavailableReason is not null)
        {
            deliveries.Stage(
                entity.Id,
                instance,
                ResponseBitrixDeliveryOutcomes.Unavailable,
                source,
                errorMessage: unavailableReason);
            return SendAttemptResult.Unavailable(unavailableReason);
        }

        if (isDuplicate)
        {
            deliveries.Stage(
                entity.Id,
                instance,
                ResponseBitrixDeliveryOutcomes.Duplicate,
                source,
                errorMessage: CandidateBitrixSendService.BuildDuplicateSummary(instance));
            return SendAttemptResult.Duplicate();
        }

        var (success, error, entityId, contactId) = await bitrixSend.SendAsync(entity, instance, ct);
        if (success)
        {
            deliveries.Stage(
                entity.Id,
                instance,
                ResponseBitrixDeliveryOutcomes.Sent,
                source,
                entityId,
                entity.BitrixEntityType,
                contactId);
            return SendAttemptResult.Sent(entityId, contactId);
        }

        deliveries.Stage(
            entity.Id,
            instance,
            ResponseBitrixDeliveryOutcomes.Error,
            source,
            bitrixEntityId: entityId,
            bitrixEntityType: entity.BitrixEntityType,
            bitrixContactId: contactId,
            errorMessage: error ?? "Ошибка отправки в Bitrix24.");
        return SendAttemptResult.Failed(error ?? "Ошибка отправки в Bitrix24.");
    }

    private static string? BuildBroadcastSummary(
        IReadOnlyList<string> sent,
        IReadOnlyList<string> duplicates,
        IReadOnlyList<string> failures)
    {
        if (duplicates.Count == 0 && failures.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (sent.Count > 0)
        {
            parts.Add($"Отправлено: {string.Join(", ", sent)}");
        }

        if (duplicates.Count > 0)
        {
            parts.Add($"Дубль: {string.Join(", ", duplicates)}");
        }

        if (failures.Count > 0)
        {
            parts.Add($"Ошибки: {string.Join("; ", failures)}");
        }

        return string.Join(". ", parts);
    }

    private enum SendAttemptOutcome
    {
        Sent,
        Duplicate,
        Unavailable,
        Failed
    }

    private sealed record SendAttemptResult(
        SendAttemptOutcome Outcome,
        string? Error = null,
        string? EntityId = null,
        string? ContactId = null)
    {
        public static SendAttemptResult Sent(string? entityId, string? contactId) =>
            new(SendAttemptOutcome.Sent, EntityId: entityId, ContactId: contactId);

        public static SendAttemptResult Duplicate() => new(SendAttemptOutcome.Duplicate);

        public static SendAttemptResult Unavailable(string error) =>
            new(SendAttemptOutcome.Unavailable, error);

        public static SendAttemptResult Failed(string error) => new(SendAttemptOutcome.Failed, error);
    }
}

public sealed record AutoDistributionResult(
    string Status,
    Guid? BitrixInstanceId,
    string? BitrixEntityId,
    string? BitrixContactId,
    string DistributionMode,
    bool IsBitrixDuplicate,
    Guid? DuplicateBitrixInstanceId,
    string? DuplicateSummary,
    string? ErrorMessage)
{
    public static AutoDistributionResult Sent(
        BitrixInstanceEntity instance,
        string entityId,
        string contactId,
        string distributionMode,
        string? summary) =>
        new(
            ResponseStatuses.Sent,
            instance.Id,
            entityId,
            contactId,
            distributionMode,
            false,
            null,
            null,
            summary ?? string.Empty);

    public static AutoDistributionResult Duplicate(BitrixInstanceEntity instance, string summary) =>
        new(
            ResponseStatuses.Duplicate,
            null,
            null,
            null,
            string.Empty,
            true,
            instance.Id,
            summary,
            summary);

    public static AutoDistributionResult ActionRequired(string message) =>
        new(
            ResponseStatuses.ActionRequired,
            null,
            null,
            null,
            string.Empty,
            false,
            null,
            null,
            message);

    public static AutoDistributionResult Error(string message) =>
        new(
            ResponseStatuses.Error,
            null,
            null,
            null,
            string.Empty,
            false,
            null,
            null,
            message);
}