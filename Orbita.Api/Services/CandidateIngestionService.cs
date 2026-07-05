using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Models;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CandidateIngestionService(
    OrbitaDbContext db,
    PhoneNormalizer phoneNormalizer,
    CandidateParser candidateParser,
    CandidateDuplicateService duplicateService,
    OfficeBitrixWebhookResolver webhookResolver,
    OfficeBitrixSettingsService officeBitrixSettings,
    BitrixClient bitrixClient,
    IOptions<OrbitaBitrixSettings> bitrixOptions,
    IPanelRealtimeNotifier panelRealtime)
{
    public async Task<WorkerCandidateIngestionResultDto> IngestBatchAsync(
        Guid workerId,
        WorkerCandidateBatchRequest request,
        CancellationToken ct = default)
    {
        var worker = await db.Workers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (worker is null)
        {
            return EmptyResult(request.Candidates.Count);
        }

        var webhookUrl = await webhookResolver.ResolvePrimaryForIngestionAsync(worker.OfficeId, ct);
        var bitrixTransmissionEnabled = await officeBitrixSettings.IsTransmissionEnabledAsync(worker.OfficeId, ct);
        var received = request.Candidates.Count;
        var ingested = 0;
        var skippedDuplicates = 0;
        var errors = 0;
        var items = new List<WorkerCandidateIngestionItemResultDto>();

        foreach (var candidate in request.Candidates)
        {
            var item = await IngestOneAsync(worker, candidate, webhookUrl, bitrixTransmissionEnabled, ct);
            items.Add(item);

            switch (item.Status)
            {
                case ResponseStatuses.Sent:
                    ingested++;
                    break;
                case ResponseStatuses.Duplicate:
                    skippedDuplicates++;
                    break;
                case ResponseStatuses.Error:
                    errors++;
                    break;
            }
        }

        if (ingested > 0 || skippedDuplicates > 0 || errors > 0)
        {
            panelRealtime.Notify(
                [
                    PanelChangeKind.Responses,
                    PanelChangeKind.Dashboard,
                    PanelChangeKind.Accounts,
                    PanelChangeKind.NavBadges
                ],
                worker.OfficeId,
                worker.Id);
        }

        return new WorkerCandidateIngestionResultDto(received, ingested, skippedDuplicates, errors, items);
    }

    private async Task<WorkerCandidateIngestionItemResultDto> IngestOneAsync(
        WorkerEntity worker,
        WorkerCandidateDto candidate,
        string? webhookUrl,
        bool bitrixTransmissionEnabled,
        CancellationToken ct)
    {
        var phoneNormalized = phoneNormalizer.Normalize(candidate.PhoneRaw);
        if (string.IsNullOrWhiteSpace(candidate.SourceResponseId) || string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return new WorkerCandidateIngestionItemResultDto(
                null,
                candidate.SourceResponseId,
                ResponseStatuses.Error,
                "Не задан SourceResponseId или телефон.");
        }

        var existing = await db.CandidateResponses
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.AccountId == candidate.AccountId && x.SourceResponseId == candidate.SourceResponseId,
                ct);
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(candidate.ChatMessagesJson)
                && !string.Equals(candidate.ChatMessagesJson, existing.ChatMessagesJson, StringComparison.Ordinal))
            {
                var tracked = await db.CandidateResponses.FirstAsync(x => x.Id == existing.Id, ct);
                tracked.ChatMessagesJson = candidate.ChatMessagesJson;
                if (string.IsNullOrWhiteSpace(tracked.MessengerUrl)
                    && !string.IsNullOrWhiteSpace(candidate.MessengerUrl))
                {
                    tracked.MessengerUrl = candidate.MessengerUrl;
                }

                await db.SaveChangesAsync(ct);
                panelRealtime.Notify(
                    [PanelChangeKind.Responses, PanelChangeKind.NavBadges],
                    worker.OfficeId,
                    worker.Id);
            }

            return new WorkerCandidateIngestionItemResultDto(
                existing.Id,
                candidate.SourceResponseId,
                existing.Status,
                existing.ErrorMessage);
        }

        var (firstName, lastName, middleName) = candidateParser.ParseName(candidate.FullName);
        var entity = new CandidateResponseEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = worker.OfficeId,
            WorkerId = worker.Id,
            WorkerName = worker.DisplayName,
            AccountId = candidate.AccountId,
            AccountName = candidate.AccountName,
            Source = string.IsNullOrWhiteSpace(candidate.Source) ? "Avito" : candidate.Source,
            SourceResponseId = candidate.SourceResponseId,
            FullName = candidate.FullName,
            FirstName = firstName,
            LastName = lastName,
            MiddleName = middleName,
            Age = candidate.Age,
            PhoneRaw = candidate.PhoneRaw,
            PhoneNormalized = phoneNormalized,
            City = candidate.City,
            Vacancy = candidate.Vacancy,
            VacancyUrl = candidate.VacancyUrl,
            MessengerUrl = candidate.MessengerUrl,
            AvitoSubProfileId = candidate.AvitoSubProfileId,
            AvitoSubProfileName = candidate.AvitoSubProfileName,
            RawText = candidate.RawText,
            ChatMessagesJson = candidate.ChatMessagesJson,
            CreatedAt = candidate.CreatedAt == default ? DateTime.UtcNow : candidate.CreatedAt,
            Status = ResponseStatuses.InProgress,
            BitrixEntityType = bitrixOptions.Value.EntityType
        };

        db.CandidateResponses.Add(entity);
        await db.SaveChangesAsync(ct);

        var checkBitrixDuplicates = bitrixTransmissionEnabled && bitrixOptions.Value.CheckDuplicatesInBitrix;
        var duplicate = await duplicateService.CheckAsync(
            entity,
            webhookUrl,
            checkBitrixDuplicates,
            ct);

        entity.IsLocalDuplicate = duplicate.IsLocalDuplicate;
        entity.IsBitrixDuplicate = duplicate.IsBitrixDuplicate;
        entity.DuplicateSummary = duplicate.Summary;
        entity.ProcessedAt = DateTime.UtcNow;

        if (duplicate.IsDuplicate)
        {
            entity.Status = ResponseStatuses.Duplicate;
            await db.SaveChangesAsync(ct);
            return new WorkerCandidateIngestionItemResultDto(
                entity.Id,
                candidate.SourceResponseId,
                entity.Status,
                entity.DuplicateSummary);
        }

        if (duplicate.ShouldDeferBitrixSend)
        {
            entity.Status = ResponseStatuses.ActionRequired;
            entity.ErrorMessage = duplicate.BitrixCheckUnavailableReason ?? duplicate.Summary;
            await db.SaveChangesAsync(ct);
            return new WorkerCandidateIngestionItemResultDto(
                entity.Id,
                candidate.SourceResponseId,
                entity.Status,
                entity.ErrorMessage);
        }

        if (!bitrixTransmissionEnabled)
        {
            entity.Status = ResponseStatuses.ActionRequired;
            entity.ErrorMessage = "Передача в Bitrix24 отключена.";
            await db.SaveChangesAsync(ct);
            return new WorkerCandidateIngestionItemResultDto(
                entity.Id,
                candidate.SourceResponseId,
                entity.Status,
                entity.ErrorMessage);
        }

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            entity.Status = ResponseStatuses.ActionRequired;
            entity.ErrorMessage = "Не настроен валидный вебхук Bitrix24 для офиса.";
            await db.SaveChangesAsync(ct);
            return new WorkerCandidateIngestionItemResultDto(
                entity.Id,
                candidate.SourceResponseId,
                entity.Status,
                entity.ErrorMessage);
        }

        var lead = MapLead(entity);
        var bitrixResult = await bitrixClient.CreateLeadAsync(lead, webhookUrl, bitrixOptions.Value, ct);
        if (bitrixResult.IsSuccess)
        {
            entity.Status = ResponseStatuses.Sent;
            entity.BitrixEntityId = bitrixResult.EntityId;
            entity.BitrixContactId = bitrixResult.ContactId;
            entity.ErrorMessage = string.Empty;
        }
        else
        {
            entity.Status = ResponseStatuses.Error;
            entity.ErrorMessage = bitrixResult.Error;
            entity.BitrixContactId = bitrixResult.ContactId;
        }

        await db.SaveChangesAsync(ct);
        return new WorkerCandidateIngestionItemResultDto(
            entity.Id,
            candidate.SourceResponseId,
            entity.Status,
            entity.ErrorMessage);
    }

    public async Task<ResendBitrixResultDto> ResendToBitrixAsync(
        Guid responseId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        var entity = await db.CandidateResponses
            .Include(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == responseId, ct);
        if (entity is null)
        {
            return new ResendBitrixResultDto(false, ResponseStatuses.Error, null, "Отклик не найден.");
        }

        if (!scope.CanAccessOffice(entity.OfficeId))
        {
            return new ResendBitrixResultDto(false, ResponseStatuses.Error, null, "Нет доступа к отклику.");
        }

        if (!await officeBitrixSettings.IsTransmissionEnabledAsync(entity.OfficeId, ct))
        {
            entity.Status = ResponseStatuses.ActionRequired;
            entity.ErrorMessage = "Передача в Bitrix24 отключена.";
            entity.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new ResendBitrixResultDto(false, entity.Status, null, entity.ErrorMessage);
        }

        var webhookUrl = await webhookResolver.ResolvePrimaryForIngestionAsync(entity.OfficeId, ct);
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            entity.Status = ResponseStatuses.ActionRequired;
            entity.ErrorMessage = "Не настроен валидный вебхук Bitrix24 для офиса.";
            entity.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new ResendBitrixResultDto(false, entity.Status, null, entity.ErrorMessage);
        }

        var duplicate = await duplicateService.CheckAsync(
            entity,
            webhookUrl,
            bitrixOptions.Value.CheckDuplicatesInBitrix,
            ct);
        entity.IsLocalDuplicate = duplicate.IsLocalDuplicate;
        entity.IsBitrixDuplicate = duplicate.IsBitrixDuplicate;
        entity.DuplicateSummary = duplicate.Summary;

        if (duplicate.IsDuplicate)
        {
            entity.Status = ResponseStatuses.Duplicate;
            entity.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new ResendBitrixResultDto(false, entity.Status, null, entity.DuplicateSummary);
        }

        if (duplicate.ShouldDeferBitrixSend)
        {
            entity.Status = ResponseStatuses.ActionRequired;
            entity.ErrorMessage = duplicate.BitrixCheckUnavailableReason ?? duplicate.Summary;
            entity.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new ResendBitrixResultDto(false, entity.Status, null, entity.ErrorMessage);
        }

        var bitrixResult = await bitrixClient.CreateLeadAsync(
            MapLead(entity),
            webhookUrl,
            bitrixOptions.Value,
            ct);
        entity.ProcessedAt = DateTime.UtcNow;
        if (bitrixResult.IsSuccess)
        {
            entity.Status = ResponseStatuses.Sent;
            entity.BitrixEntityId = bitrixResult.EntityId;
            entity.BitrixContactId = bitrixResult.ContactId;
            entity.ErrorMessage = string.Empty;
            await db.SaveChangesAsync(ct);
            return new ResendBitrixResultDto(true, entity.Status, entity.BitrixEntityId, null);
        }

        entity.Status = ResponseStatuses.Error;
        entity.ErrorMessage = bitrixResult.Error;
        entity.BitrixContactId = bitrixResult.ContactId;
        await db.SaveChangesAsync(ct);
        return new ResendBitrixResultDto(false, entity.Status, null, entity.ErrorMessage);
    }

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

    private static WorkerCandidateIngestionResultDto EmptyResult(int received) =>
        new(received, 0, 0, received, []);
}