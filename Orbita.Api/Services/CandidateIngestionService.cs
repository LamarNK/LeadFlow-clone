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
    DistributionRouteService distributionRoute,
    DistributionEngine distributionEngine,
    CandidateAutoDistributionService autoDistribution,
    ManualBitrixSendService manualBitrixSend,
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

        var autoEnabled = await distributionRoute.IsAutoDistributionEnabledAsync(worker.OfficeId, ct);
        var received = request.Candidates.Count;
        var ingested = 0;
        var skippedDuplicates = 0;
        var errors = 0;
        var items = new List<WorkerCandidateIngestionItemResultDto>();

        foreach (var candidate in request.Candidates)
        {
            var item = await IngestOneAsync(worker, candidate, autoEnabled, ct);
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
        bool autoEnabled,
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
            CardFingerprint = ResolveCardFingerprint(candidate),
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

        var localDuplicate = await duplicateService.FindLocalDuplicateAsync(
            entity.OfficeId,
            entity.PhoneNormalized,
            entity.Id,
            ct);
        entity.IsLocalDuplicate = localDuplicate is not null;
        entity.ProcessedAt = DateTime.UtcNow;

        if (localDuplicate is not null)
        {
            entity.Status = ResponseStatuses.Duplicate;
            entity.DuplicateSummary = "Локальный дубль в Орбите";
            await db.SaveChangesAsync(ct);
            return new WorkerCandidateIngestionItemResultDto(
                entity.Id,
                candidate.SourceResponseId,
                entity.Status,
                entity.DuplicateSummary);
        }

        if (!autoEnabled)
        {
            entity.Status = ResponseStatuses.ActionRequired;
            entity.ErrorMessage = "Ожидает действия оператора.";
            await db.SaveChangesAsync(ct);
            return new WorkerCandidateIngestionItemResultDto(
                entity.Id,
                candidate.SourceResponseId,
                entity.Status,
                entity.ErrorMessage);
        }

        var plan = await distributionEngine.GetPlanAsync(worker.OfficeId, ct);
        var distributionResult = await autoDistribution.DistributeAsync(entity, plan, ct);
        ApplyDistributionResult(entity, distributionResult);
        if (distributionResult.Status == ResponseStatuses.Error)
        {
            panelRealtime.Notify(
                [PanelChangeKind.Responses, PanelChangeKind.NavBadges],
                worker.OfficeId,
                worker.Id,
                entity.ErrorMessage);
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
        var entity = await db.CandidateResponses.FirstOrDefaultAsync(x => x.Id == responseId, ct);
        if (entity is null)
        {
            return new ResendBitrixResultDto(false, ResponseStatuses.Error, null, "Отклик не найден.");
        }

        if (!scope.CanAccessOffice(entity.OfficeId))
        {
            return new ResendBitrixResultDto(false, ResponseStatuses.Error, null, "Нет доступа к отклику.");
        }

        if (entity.BitrixInstanceId is Guid)
        {
            var result = await manualBitrixSend.SendAsync(responseId, entity.BitrixInstanceId.Value, scope, ct);
            return new ResendBitrixResultDto(
                result.Success,
                result.Status,
                result.BitrixEntityId,
                result.ErrorMessage);
        }

        var plan = await distributionEngine.GetPlanAsync(entity.OfficeId, ct);
        var distributionResult = await autoDistribution.DistributeAsync(entity, plan, ct);
        ApplyDistributionResult(entity, distributionResult);
        entity.ProcessedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new ResendBitrixResultDto(
            distributionResult.Status == ResponseStatuses.Sent,
            distributionResult.Status,
            distributionResult.BitrixEntityId,
            distributionResult.ErrorMessage);
    }

    private static void ApplyDistributionResult(CandidateResponseEntity entity, AutoDistributionResult result)
    {
        entity.Status = result.Status;
        entity.BitrixInstanceId = result.BitrixInstanceId;
        entity.BitrixEntityId = result.BitrixEntityId ?? string.Empty;
        entity.BitrixContactId = result.BitrixContactId ?? string.Empty;
        entity.DistributionMode = string.IsNullOrWhiteSpace(result.DistributionMode)
            ? DistributionModes.Auto
            : result.DistributionMode;
        entity.IsBitrixDuplicate = result.IsBitrixDuplicate;
        entity.DuplicateBitrixInstanceId = result.DuplicateBitrixInstanceId;
        entity.DuplicateSummary = result.DuplicateSummary ?? string.Empty;
        entity.ErrorMessage = result.ErrorMessage ?? string.Empty;
    }

    private static string ResolveCardFingerprint(WorkerCandidateDto candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.CardFingerprint))
        {
            return candidate.CardFingerprint.Trim();
        }

        return AvitoResponseCardFingerprint.Build(
            candidate.FullName,
            candidate.Vacancy,
            candidate.City,
            candidate.VacancyUrl,
            candidate.MessengerUrl,
            AvitoResponseCardFingerprint.NormalizeAgeText(null, candidate.Age));
    }

    private static WorkerCandidateIngestionResultDto EmptyResult(int received) =>
        new(received, 0, 0, received, []);
}