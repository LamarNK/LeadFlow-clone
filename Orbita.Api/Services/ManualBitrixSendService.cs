using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class ManualBitrixSendService(
    OrbitaDbContext db,
    CandidateDuplicateService duplicateService,
    BitrixDuplicateCheckAllService bitrixDuplicateCheck,
    CandidateBitrixSendService bitrixSend,
    ResponseBitrixDeliveryService deliveries,
    IPanelRealtimeNotifier panelRealtime)
{
    public async Task<SendBitrixResultDto> SendAsync(
        Guid responseId,
        Guid bitrixInstanceId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var entity = await db.CandidateResponses
            .Include(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == responseId, ct);
        if (entity is null)
        {
            return new SendBitrixResultDto(false, ResponseStatuses.Error, null, null, null, "Отклик не найден.");
        }

        if (!scope.CanAccessOffice(entity.OfficeId))
        {
            return new SendBitrixResultDto(false, ResponseStatuses.Error, null, null, null, "Нет доступа к отклику.");
        }

        var instance = await db.BitrixInstances
            .FirstOrDefaultAsync(x => x.Id == bitrixInstanceId && x.OfficeId == entity.OfficeId, ct);
        if (instance is null || !instance.IsEnabled)
        {
            return new SendBitrixResultDto(false, ResponseStatuses.Error, null, null, null, "Битрикс не найден или отключён.");
        }

        var localDuplicate = await duplicateService.FindLocalDuplicateAsync(
            entity.OfficeId,
            entity.PersonId,
            entity.Id,
            ct);
        if (localDuplicate is not null)
        {
            entity.Status = ResponseStatuses.Duplicate;
            entity.IsLocalDuplicate = true;
            entity.DuplicateSummary = "Локальный дубль в Орбите";
            entity.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new SendBitrixResultDto(false, entity.Status, null, instance.Id, instance.Name, entity.DuplicateSummary);
        }

        var (isDuplicate, unavailableReason) = await bitrixDuplicateCheck.CheckInInstanceAsync(
            instance,
            CandidatePersonMatchService.ToProfile(entity),
            ct);
        if (unavailableReason is not null)
        {
            deliveries.Stage(
                entity.Id,
                instance,
                ResponseBitrixDeliveryOutcomes.Unavailable,
                DistributionModes.Manual,
                errorMessage: unavailableReason);
            entity.Status = ResponseStatuses.ActionRequired;
            entity.ErrorMessage = unavailableReason;
            entity.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            Notify(entity, unavailableReason);
            return new SendBitrixResultDto(false, entity.Status, null, instance.Id, instance.Name, unavailableReason);
        }

        if (isDuplicate)
        {
            var duplicateSummary = CandidateBitrixSendService.BuildDuplicateSummary(instance);
            deliveries.Stage(
                entity.Id,
                instance,
                ResponseBitrixDeliveryOutcomes.Duplicate,
                DistributionModes.Manual,
                errorMessage: duplicateSummary);
            entity.Status = ResponseStatuses.Duplicate;
            entity.IsBitrixDuplicate = true;
            entity.DuplicateBitrixInstanceId = instance.Id;
            entity.DuplicateSummary = duplicateSummary;
            entity.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new SendBitrixResultDto(false, entity.Status, null, instance.Id, instance.Name, entity.DuplicateSummary);
        }

        var (success, error, entityId, contactId) = await bitrixSend.SendAsync(entity, instance, ct);
        entity.BitrixInstanceId = instance.Id;
        entity.DistributionMode = DistributionModes.Manual;
        entity.ProcessedAt = DateTime.UtcNow;
        if (success)
        {
            deliveries.Stage(
                entity.Id,
                instance,
                ResponseBitrixDeliveryOutcomes.Sent,
                DistributionModes.Manual,
                entityId,
                entity.BitrixEntityType,
                contactId);
            entity.Status = ResponseStatuses.Sent;
            entity.BitrixEntityId = entityId ?? string.Empty;
            entity.BitrixContactId = contactId ?? string.Empty;
            entity.ErrorMessage = string.Empty;
            entity.IsBitrixDuplicate = false;
            entity.DuplicateBitrixInstanceId = null;
            entity.DuplicateSummary = string.Empty;
            await db.SaveChangesAsync(ct);
            Notify(entity);
            return new SendBitrixResultDto(true, entity.Status, entity.BitrixEntityId, instance.Id, instance.Name, null);
        }

        var errorMessage = error ?? "Ошибка отправки в Bitrix24.";
        deliveries.Stage(
            entity.Id,
            instance,
            ResponseBitrixDeliveryOutcomes.Error,
            DistributionModes.Manual,
            bitrixEntityId: entityId,
            bitrixEntityType: entity.BitrixEntityType,
            bitrixContactId: contactId,
            errorMessage: errorMessage);
        entity.Status = ResponseStatuses.Error;
        entity.ErrorMessage = errorMessage;
        entity.BitrixContactId = contactId ?? string.Empty;
        await db.SaveChangesAsync(ct);
        Notify(entity, entity.ErrorMessage);
        return new SendBitrixResultDto(false, entity.Status, null, instance.Id, instance.Name, entity.ErrorMessage);
    }

    private void Notify(CandidateResponseEntity entity, string? operatorMessage = null)
    {
        panelRealtime.Notify(
            [
                PanelChangeKind.Responses,
                PanelChangeKind.Dashboard,
                PanelChangeKind.NavBadges
            ],
            entity.OfficeId,
            entity.WorkerId,
            operatorMessage);
    }
}