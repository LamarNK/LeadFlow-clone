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
    CandidatePersonMatchService personMatch,
    CandidatePersonPhoneService personPhone,
    DistributionEngine distributionEngine,
    CandidateAutoDistributionService autoDistribution,
    ManualBitrixSendService manualBitrixSend,
    ResponseDeliveryService responseDelivery,
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

        var received = request.Candidates.Count;
        var ingested = 0;
        var skippedDuplicates = 0;
        var errors = 0;
        var items = new List<WorkerCandidateIngestionItemResultDto>();

        foreach (var candidate in request.Candidates)
        {
            var item = await IngestOneAsync(worker, candidate, ct);
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
            return await UpdateExistingResponseAsync(worker, candidate, existing, phoneNormalized, ct);
        }

        var (firstName, lastName, middleName) = candidateParser.ParseName(candidate.FullName);
        var genderResolution = CandidateGenderResolver.Resolve(
            candidate.FullName,
            candidate.Gender,
            candidate.RawText);
        var storedGender = CandidateGenderResolver.ToStoredGender(genderResolution);
        byte[]? avatarImage = null;
        string? avatarContentType = null;
        if (CandidateResponseAvatar.TryDecode(
                candidate.AvatarContentType,
                candidate.AvatarImageBase64,
                out var decodedAvatar,
                out var detectedAvatarContentType))
        {
            avatarImage = decodedAvatar;
            avatarContentType = detectedAvatarContentType;
        }

        var workerFilters = ResponseCollectionFilters.NormalizeLegacy(
            worker.ResponseFilterEnabled,
            worker.ResponseFilterExcludeFemale,
            worker.ResponseFilterMaxAge,
            worker.ResponseFilterExcludeMale,
            worker.ResponseFilterMaxAgeMale,
            worker.ResponseFilterMaxAgeFemale,
            worker.ResponseFilterMaxAgeDays);
        var filterResult = ResponseCollectionFilter.Evaluate(
            candidate.Age,
            genderResolution.Gender is CandidateGenders.Unknown ? null : genderResolution.Gender,
            workerFilters);
        if (!filterResult.Pass)
        {
            // Не пишем в CandidateResponses; статус вне ResponseStatuses — не считается error/duplicate в counters.
            return new WorkerCandidateIngestionItemResultDto(
                null,
                candidate.SourceResponseId,
                "Filtered",
                $"Отсечён фильтром сбора: {filterResult.RejectReason}.");
        }

        // Проверка давности отклика (пропускать старше N дней).
        var collectedAt = candidate.CollectedAt == default ? DateTime.UtcNow : candidate.CollectedAt;
        var responseAt = candidate.CreatedAt == default ? collectedAt : candidate.CreatedAt;
        var ageFilterResult = ResponseCollectionFilter.EvaluateResponseAge(responseAt, workerFilters);
        if (!ageFilterResult.Pass)
        {
            return new WorkerCandidateIngestionItemResultDto(
                null,
                candidate.SourceResponseId,
                "Filtered",
                $"Отсечён фильтром давности: {ageFilterResult.RejectReason}.");
        }
        var profile = CandidatePersonMatchService.ToProfile(
            candidate.FullName,
            candidate.Age,
            candidate.City,
            phoneNormalized,
            responseAt);

        // Collection pool: global person match (no office filter).
        var matchedPerson = await personMatch.FindMatchingPersonAsync(officeId: null, profile, ct);
        var phoneMetricKind = ResponsePhoneMetricKinds.Normalize(candidate.PhoneMetricKind);
        // PhoneChanged сохраняет новый отклик, но существующую CRM-карточку
        // дополняем номером вместо повторной доставки кандидата в другой офис.
        // PhoneUnchanged — метка стабильного номера; дублем её делает только совпадение кандидата.
        var alreadyInCrm = matchedPerson is not null && await db.CrmCandidateCards.AsNoTracking()
            .AnyAsync(x => x.Response.PersonId == matchedPerson.Id, ct);
        var isLocalDuplicate = matchedPerson is not null
            && (phoneMetricKind != ResponsePhoneMetricKinds.PhoneChanged || alreadyInCrm);

        CandidatePersonEntity person;
        if (matchedPerson is not null)
        {
            person = await db.CandidatePersons.FirstAsync(x => x.Id == matchedPerson.Id, ct);
        }
        else
        {
            person = personMatch.CreatePerson(
                officeId: null,
                candidate.FullName,
                firstName,
                lastName,
                middleName,
                candidate.Age,
                candidate.City,
                candidate.PhoneRaw,
                phoneNormalized,
                collectedAt);
            db.CandidatePersons.Add(person);
            await db.SaveChangesAsync(ct);
        }

        var entity = new CandidateResponseEntity
        {
            Id = Guid.NewGuid(),
            PersonId = person.Id,
            // Stay in collection pool until delivery assigns a CRM/Bitrix office.
            OfficeId = null,
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
            Gender = storedGender,
            Citizenship = CandidateCitizenshipResolver.Resolve(
                candidate.Citizenship,
                candidate.RawText,
                candidate.ChatMessagesJson),
            PhoneRaw = candidate.PhoneRaw,
            PhoneNormalized = phoneNormalized,
            City = candidate.City,
            Vacancy = candidate.Vacancy,
            SourceUrl = candidate.VacancyUrl,
            VacancyUrl = candidate.VacancyUrl,
            MessengerUrl = candidate.MessengerUrl,
            AvatarContentType = avatarContentType,
            AvatarImage = avatarImage,
            AvitoSubProfileId = candidate.AvitoSubProfileId,
            AvitoSubProfileName = candidate.AvitoSubProfileName,
            RawText = candidate.RawText,
            ChatMessagesJson = candidate.ChatMessagesJson,
            CreatedAt = responseAt,
            CollectedAt = collectedAt,
            Status = ResponseStatuses.InProgress,
            BitrixEntityType = bitrixOptions.Value.EntityType,
            PhoneMetricKind = phoneMetricKind,
            PreviousPhoneRaw = candidate.PreviousPhoneRaw?.Trim() ?? string.Empty,
            PreviousPhoneNormalized = candidate.PreviousPhoneNormalized?.Trim() ?? string.Empty,
            PhoneUnchangedHours = candidate.PhoneUnchangedHours,
            PhoneChangedAtUtc = candidate.PhoneChangedAtUtc
        };

        db.CandidateResponses.Add(entity);
        await db.SaveChangesAsync(ct);

        await personPhone.ApplyPhoneFromResponseAsync(person, candidate.PhoneRaw, phoneNormalized, entity.Id, ct);
        var enrichedOffices = await personPhone.StageCrmContactAsync(person.Id, candidate.PhoneRaw, phoneNormalized, ct);

        entity.IsLocalDuplicate = isLocalDuplicate;
        entity.ProcessedAt = DateTime.UtcNow;

        if (isLocalDuplicate)
        {
            entity.Status = ResponseStatuses.Duplicate;
            entity.DuplicateSummary = phoneMetricKind switch
            {
                ResponsePhoneMetricKinds.PhoneUnchanged =>
                    ResponsePhoneMetricKinds.FormatLabel(
                        phoneMetricKind,
                        candidate.PhoneUnchangedHours),
                ResponsePhoneMetricKinds.PhoneChanged =>
                    ResponsePhoneMetricKinds.FormatLabel(
                        phoneMetricKind,
                        previousPhone: candidate.PreviousPhoneRaw ?? candidate.PreviousPhoneNormalized),
                _ => "Локальный дубль: найден существующий кандидат по ФИО."
            };
            await db.SaveChangesAsync(ct);
            foreach (var crmOfficeId in enrichedOffices) panelRealtime.Notify([PanelChangeKind.Crm], crmOfficeId);
            return new WorkerCandidateIngestionItemResultDto(
                entity.Id,
                candidate.SourceResponseId,
                entity.Status,
                entity.DuplicateSummary);
        }

        // Delivery (CRM and/or Bitrix) is driven by worker auto flags — not office route alone.
        await responseDelivery.ApplyAutoDeliveryAsync(entity, worker, ct);
        await db.Entry(entity).ReloadAsync(ct);

        if (entity.Status == ResponseStatuses.Error)
        {
            panelRealtime.Notify(
                [PanelChangeKind.Responses, PanelChangeKind.NavBadges],
                worker.OfficeId,
                worker.Id,
                entity.ErrorMessage);
        }

        return new WorkerCandidateIngestionItemResultDto(
            entity.Id,
            candidate.SourceResponseId,
            entity.Status,
            entity.ErrorMessage);
    }

    private async Task<WorkerCandidateIngestionItemResultDto> UpdateExistingResponseAsync(
        WorkerEntity worker,
        WorkerCandidateDto candidate,
        CandidateResponseEntity existing,
        string phoneNormalized,
        CancellationToken ct)
    {
        var tracked = await db.CandidateResponses
            .FirstAsync(x => x.Id == existing.Id, ct);

        var changed = false;
        if (!string.IsNullOrWhiteSpace(candidate.ChatMessagesJson)
            && !string.Equals(candidate.ChatMessagesJson, tracked.ChatMessagesJson, StringComparison.Ordinal))
        {
            tracked.ChatMessagesJson = candidate.ChatMessagesJson;
            changed = true;
        }

        if (TryBackfillCreatedAtFromChat(tracked, candidate, out var chatResponseAt))
        {
            tracked.CreatedAt = chatResponseAt;
            changed = true;
        }

        // phone-watch шлёт тот же SourceResponseId повторно. Обновляем поля Avito,
        // кроме тех, что оператор правил вручную. Пустой повторный парсинг ничего не затирает.
        var locks = tracked.OperatorLockedFields;
        var city = candidate.City?.Trim() ?? string.Empty;
        if (TryApplyUnlocked(tracked.City, city, locks, ResponseOperatorLocks.City, out var nextCity))
        {
            tracked.City = nextCity;
            changed = true;
        }

        var vacancy = candidate.Vacancy?.Trim() ?? string.Empty;
        if (TryApplyUnlocked(tracked.Vacancy, vacancy, locks, ResponseOperatorLocks.Vacancy, out var nextVacancy))
        {
            tracked.Vacancy = nextVacancy;
            changed = true;
        }

        if (!ResponseOperatorLocks.Contains(locks, ResponseOperatorLocks.Age)
            && tracked.Age is null
            && candidate.Age is int incomingAge
            && incomingAge > 0)
        {
            tracked.Age = incomingAge;
            changed = true;
        }

        var incomingGender = CandidateGenderResolver.ToStoredGender(
            CandidateGenderResolver.Resolve(candidate.FullName, candidate.Gender, candidate.RawText));
        if (TryApplyUnlocked(tracked.Gender, incomingGender, locks, ResponseOperatorLocks.Gender, out var nextGender))
        {
            tracked.Gender = nextGender;
            changed = true;
        }

        var citizenship = CandidateCitizenshipResolver.Resolve(
            candidate.Citizenship,
            candidate.RawText,
            candidate.ChatMessagesJson,
            tracked.RawText,
            tracked.ChatMessagesJson);
        if (TryApplyUnlocked(tracked.Citizenship, citizenship, locks, ResponseOperatorLocks.Citizenship, out var nextCitizenship))
        {
            tracked.Citizenship = nextCitizenship;
            changed = true;
        }

        var messengerUrl = candidate.MessengerUrl?.Trim() ?? string.Empty;
        if (TryApplyUnlocked(tracked.MessengerUrl, messengerUrl, locks, ResponseOperatorLocks.MessengerUrl, out var nextMessenger))
        {
            tracked.MessengerUrl = nextMessenger;
            changed = true;
        }

        var vacancyUrl = candidate.VacancyUrl?.Trim() ?? string.Empty;
        if (TryApplyUnlocked(tracked.VacancyUrl, vacancyUrl, locks, ResponseOperatorLocks.VacancyUrl, out var nextVacancyUrl))
        {
            tracked.VacancyUrl = nextVacancyUrl;
            changed = true;
        }

        if (TryApplyUnlocked(tracked.SourceUrl, vacancyUrl, locks, ResponseOperatorLocks.SourceUrl, out var nextSourceUrl))
        {
            tracked.SourceUrl = nextSourceUrl;
            changed = true;
        }

        var rawText = candidate.RawText?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(rawText)
            && !string.Equals(tracked.RawText, rawText, StringComparison.Ordinal))
        {
            tracked.RawText = rawText;
            changed = true;
        }

        if (tracked.AvatarImage is null
            && CandidateResponseAvatar.TryDecode(
                candidate.AvatarContentType,
                candidate.AvatarImageBase64,
                out var decodedAvatar,
                out var detectedAvatarContentType))
        {
            tracked.AvatarImage = decodedAvatar;
            tracked.AvatarContentType = detectedAvatarContentType;
            changed = true;
        }

        if (changed)
        {
            var person = await db.CandidatePersons.FirstOrDefaultAsync(x => x.Id == tracked.PersonId, ct);
            if (person is not null)
            {
                var personTouched = false;
                if (!ResponseOperatorLocks.Contains(locks, ResponseOperatorLocks.City)
                    && !string.IsNullOrWhiteSpace(tracked.City)
                    && !string.Equals(person.City, tracked.City, StringComparison.Ordinal))
                {
                    person.City = tracked.City;
                    personTouched = true;
                }

                if (!ResponseOperatorLocks.Contains(locks, ResponseOperatorLocks.Age)
                    && tracked.Age is int personAge
                    && person.Age != personAge)
                {
                    person.Age = personAge;
                    personTouched = true;
                }

                if (personTouched)
                {
                    person.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
        }

        var phoneChanged = !string.Equals(tracked.PhoneNormalized, phoneNormalized, StringComparison.Ordinal);
        Guid[] phoneChangedOffices = [];
        if (phoneChanged)
        {
            // Temporary Avito numbers can change after CRM/Bitrix send — keep the same response
            // and append person/response phone history (do not re-run auto delivery).
            var previousRaw = tracked.PhoneRaw;
            var previousNormalized = tracked.PhoneNormalized;
            // Seed the previous dialable number before updating the response's main number.
            await personPhone.StageCrmContactAsync(tracked.PersonId, previousRaw, previousNormalized, ct);
            tracked.PhoneRaw = candidate.PhoneRaw;
            tracked.PhoneNormalized = phoneNormalized;

            var metricKind = ResponsePhoneMetricKinds.Normalize(candidate.PhoneMetricKind);
            if (metricKind == ResponsePhoneMetricKinds.PhoneChanged
                || !string.IsNullOrWhiteSpace(candidate.PreviousPhoneNormalized)
                || !string.IsNullOrWhiteSpace(candidate.PreviousPhoneRaw))
            {
                tracked.PhoneMetricKind = ResponsePhoneMetricKinds.PhoneChanged;
                tracked.PreviousPhoneRaw = candidate.PreviousPhoneRaw?.Trim()
                    ?? previousRaw;
                tracked.PreviousPhoneNormalized = candidate.PreviousPhoneNormalized?.Trim()
                    ?? previousNormalized;
                tracked.PhoneChangedAtUtc = candidate.PhoneChangedAtUtc ?? DateTime.UtcNow;
            }
            else
            {
                tracked.PhoneMetricKind = ResponsePhoneMetricKinds.PhoneChanged;
                tracked.PreviousPhoneRaw = previousRaw;
                tracked.PreviousPhoneNormalized = previousNormalized;
                tracked.PhoneChangedAtUtc = DateTime.UtcNow;
            }

            changed = true;

            var person = await db.CandidatePersons.FirstAsync(x => x.Id == tracked.PersonId, ct);
            await personPhone.ApplyPhoneFromResponseAsync(
                person,
                candidate.PhoneRaw,
                phoneNormalized,
                tracked.Id,
                ct);
            phoneChangedOffices = await personPhone.StageCrmContactAsync(tracked.PersonId, candidate.PhoneRaw, phoneNormalized, ct);
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
            foreach (var crmOfficeId in phoneChangedOffices) panelRealtime.Notify([PanelChangeKind.Crm], crmOfficeId);
            panelRealtime.Notify(
                [PanelChangeKind.Responses, PanelChangeKind.NavBadges],
                worker.OfficeId,
                worker.Id);
        }

        return new WorkerCandidateIngestionItemResultDto(
            tracked.Id,
            candidate.SourceResponseId,
            tracked.Status,
            tracked.ErrorMessage);
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

        var workerOfficeId = entity.WorkerId is Guid wid
            ? await db.Workers.AsNoTracking()
                .Where(x => x.Id == wid)
                .Select(x => (Guid?)x.OfficeId)
                .FirstOrDefaultAsync(ct)
            : null;
        if (!scope.CanAccessResponse(entity.OfficeId, workerOfficeId))
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

        var officeId = entity.OfficeId
            ?? await db.Workers.AsNoTracking()
                .Where(x => x.Id == entity.WorkerId)
                .Select(x => (Guid?)x.OfficeId)
                .FirstOrDefaultAsync(ct);
        if (officeId is null)
        {
            return new ResendBitrixResultDto(false, ResponseStatuses.ActionRequired, null, "Укажите офис для отправки.");
        }

        var deliver = await responseDelivery.DeliverAsync(
            entity.Id,
            new DeliverResponseRequest(officeId, ToCrm: false, ToBitrix: true, UseBitrixRoute: true),
            scope,
            DistributionModes.Auto,
            ct);
        return new ResendBitrixResultDto(
            deliver.Success,
            deliver.Status,
            deliver.Channels.FirstOrDefault(x => x.Channel == "Bitrix")?.BitrixEntityId,
            deliver.ErrorMessage);
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

    // Fallback CreatedAt=CollectedAt заменяем датой из чата; известную дату отклика не трогаем.
    private static bool TryBackfillCreatedAtFromChat(
        CandidateResponseEntity tracked,
        WorkerCandidateDto candidate,
        out DateTime chatResponseAt)
    {
        chatResponseAt = tracked.CreatedAt;
        if (tracked.CollectedAt == default || tracked.CreatedAt != tracked.CollectedAt)
        {
            return false;
        }

        var incoming = ResolveIncomingResponseAt(candidate);
        if (incoming is null || incoming.Value >= tracked.CreatedAt)
        {
            return false;
        }

        chatResponseAt = incoming.Value;
        return true;
    }

    private static DateTime? ResolveIncomingResponseAt(WorkerCandidateDto candidate)
    {
        if (candidate.CreatedAt != default
            && (candidate.CollectedAt == default || candidate.CreatedAt != candidate.CollectedAt))
        {
            return candidate.CreatedAt;
        }

        return AvitoChatResponseAt.TryGetUtc(candidate.ChatMessagesJson);
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

    private static bool TryApplyUnlocked(
        string current,
        string incoming,
        string locks,
        string field,
        out string next)
    {
        next = current ?? string.Empty;
        var value = incoming?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !string.IsNullOrWhiteSpace(next)
            || ResponseOperatorLocks.Contains(locks, field))
        {
            return false;
        }

        next = value;
        return true;
    }

    private static WorkerCandidateIngestionResultDto EmptyResult(int received) =>
        new(received, 0, 0, received, []);
}
