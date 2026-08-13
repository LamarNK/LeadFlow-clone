using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services.Bitrix;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class BitrixCrmImportService(
    OrbitaDbContext db,
    BitrixInstanceService bitrixInstances,
    IBitrixCrmImportClient client,
    PhoneNormalizer phoneNormalizer,
    CandidateParser candidateParser,
    IPanelRealtimeNotifier? panelRealtime = null)
{
    private const int MaxImportDeals = 1000;
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex BbCodeTagRegex = new(
        @"\[/?[a-z][a-z0-9]*(?:=[^\]]*)?\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WhiteSpaceRegex = new("\\s+", RegexOptions.Compiled);

    public async Task<(BitrixCrmImportPreviewDto? Preview, string? Error)> PreviewAsync(
        Guid bitrixInstanceId,
        OfficeScope scope,
        Guid? officeId,
        BitrixCrmImportPreviewRequest request,
        CancellationToken ct = default)
    {
        var context = await LoadContextAsync(
            bitrixInstanceId,
            scope,
            officeId,
            request.CategoryId,
            request.StageNames,
            null,
            ct);
        if (context.Error is not null)
        {
            return (null, context.Error);
        }

        var preview = await BuildPreviewAsync(context, ct);
        return (preview, null);
    }

    public async Task<(BitrixCrmImportResultDto? Result, string? Error)> ImportAsync(
        Guid bitrixInstanceId,
        OfficeScope scope,
        Guid? officeId,
        BitrixCrmImportExecuteRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        var selectedDealIds = request.DealIds?
            .Where(x => x > 0)
            .Distinct()
            .ToArray();
        var context = await LoadContextAsync(
            bitrixInstanceId,
            scope,
            officeId,
            request.CategoryId,
            request.StageNames,
            selectedDealIds,
            ct);
        if (context.Error is not null)
        {
            return (null, context.Error);
        }

        var preview = await BuildPreviewAsync(context, ct);
        var selected = selectedDealIds is null
            ? null
            : selectedDealIds.ToHashSet();
        var previewById = preview.Deals.ToDictionary(x => x.DealId);
        var managers = await LoadOrbitaManagersAsync(context.OfficeId, ct);
        var managerMatches = BuildManagerMatches(context.Snapshot.Users, managers);
        var items = new List<BitrixCrmImportItemResultDto>();

        foreach (var deal in context.Snapshot.Deals.Where(x => selected is null || selected.Contains(x.Id)))
        {
            var dealPreview = previewById[deal.Id];
            if (dealPreview.Action == BitrixCrmImportActions.AlreadyImported)
            {
                items.Add(new BitrixCrmImportItemResultDto(
                    deal.Id,
                    true,
                    dealPreview.Action,
                    dealPreview.ExistingCardId,
                    null));
                continue;
            }

            if (dealPreview.Action == BitrixCrmImportActions.MissingPhone)
            {
                items.Add(new BitrixCrmImportItemResultDto(
                    deal.Id,
                    false,
                    dealPreview.Action,
                    null,
                    dealPreview.Warning));
                continue;
            }

            try
            {
                var cardId = await ImportDealAsync(
                    context,
                    deal,
                    dealPreview,
                    managerMatches,
                    actorUserId,
                    ct);
                items.Add(new BitrixCrmImportItemResultDto(
                    deal.Id,
                    true,
                    dealPreview.Action,
                    cardId,
                    null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                items.Add(new BitrixCrmImportItemResultDto(
                    deal.Id,
                    false,
                    dealPreview.Action,
                    dealPreview.ExistingCardId,
                    ex.Message));
            }
        }

        panelRealtime?.Notify([PanelChangeKind.Crm], context.OfficeId);
        return (new BitrixCrmImportResultDto(
            items.Count,
            items.Count(x => x.Success && x.Action == BitrixCrmImportActions.Create),
            items.Count(x => x.Success && x.Action == BitrixCrmImportActions.UpdateExisting),
            items.Count(x => x.Success && x.Action == BitrixCrmImportActions.AlreadyImported),
            items.Count(x => !x.Success),
            items), null);
    }

    private async Task<Guid> ImportDealAsync(
        ImportContext context,
        BitrixImportDeal deal,
        BitrixCrmImportDealPreviewDto preview,
        IReadOnlyDictionary<long, ManagerMatch> managerMatches,
        string actorUserId,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        var phoneRaw = deal.Contact?.Phone.Trim() ?? string.Empty;
        var phoneNormalized = phoneNormalizer.Normalize(phoneRaw);
        var fullName = ResolveFullName(deal);
        var age = ResolveAge(deal, context.Snapshot.DealFieldTitles);
        var city = ResolveField(deal, context.Snapshot.DealFieldTitles, ["город"]);
        var vacancy = ResolveField(deal, context.Snapshot.DealFieldTitles, ["вакансия", "профессия"]);
        var (firstName, lastName, middleName) = candidateParser.ParseName(fullName);
        var sourceResponseId = BuildSourceResponseId(context.PortalHost, deal.Id);
        var managerUserId = deal.ResponsibleId is long responsibleId
                            && managerMatches.TryGetValue(responsibleId, out var match)
            ? match.OrbitaUserId
            : null;
        CrmCandidateCardEntity card;
        CandidateResponseEntity response;

        if (preview.ExistingCardId is Guid existingCardId)
        {
            card = await db.CrmCandidateCards
                .Include(x => x.Response)
                .ThenInclude(x => x.Person)
                .FirstAsync(x => x.Id == existingCardId && x.OfficeId == context.OfficeId, ct);
            response = card.Response;
            if (string.IsNullOrWhiteSpace(response.SourceResponseId))
            {
                response.SourceResponseId = sourceResponseId;
            }
            response.BitrixEntityType = "DEAL";
            response.BitrixEntityId = deal.Id.ToString(CultureInfo.InvariantCulture);
            response.BitrixInstanceId = context.InstanceId;
            response.AccountName = FirstNonEmpty(response.AccountName, context.PortalHost);
            response.FullName = FirstNonEmpty(response.FullName, fullName);
            response.FirstName = FirstNonEmpty(response.FirstName, firstName);
            response.LastName = FirstNonEmpty(response.LastName, lastName);
            response.MiddleName = FirstNonEmpty(response.MiddleName, middleName);
            response.Age = age ?? response.Age;
            response.City = FirstNonEmpty(city, response.City);
            response.Vacancy = FirstNonEmpty(vacancy, response.Vacancy);
            response.RawText = FirstNonEmpty(response.RawText, deal.Comments);
            response.Person.FullName = FirstNonEmpty(response.Person.FullName, fullName);
            response.Person.FirstName = FirstNonEmpty(response.Person.FirstName, firstName);
            response.Person.LastName = FirstNonEmpty(response.Person.LastName, lastName);
            response.Person.MiddleName = FirstNonEmpty(response.Person.MiddleName, middleName);
            response.Person.Age = age ?? response.Person.Age;
            response.Person.City = FirstNonEmpty(city, response.Person.City);
            response.Person.UpdatedAtUtc = now;
        }
        else
        {
            var person = new CandidatePersonEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = context.OfficeId,
                FullName = fullName,
                FirstName = firstName,
                LastName = lastName,
                MiddleName = middleName,
                Age = age,
                City = city,
                PhoneRaw = phoneRaw,
                PhoneNormalized = phoneNormalized,
                CreatedAtUtc = deal.CreatedAtUtc,
                UpdatedAtUtc = now
            };
            response = new CandidateResponseEntity
            {
                Id = Guid.NewGuid(),
                PersonId = person.Id,
                OfficeId = context.OfficeId,
                AccountId = Guid.Empty,
                AccountName = context.PortalHost,
                Source = "Bitrix24",
                SourceResponseId = sourceResponseId,
                FullName = fullName,
                FirstName = firstName,
                LastName = lastName,
                MiddleName = middleName,
                Age = age,
                PhoneRaw = phoneRaw,
                PhoneNormalized = phoneNormalized,
                City = city,
                Vacancy = vacancy,
                Status = ResponseStatuses.Sent,
                BitrixEntityType = "DEAL",
                BitrixEntityId = deal.Id.ToString(CultureInfo.InvariantCulture),
                BitrixContactId = deal.Contact?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                BitrixInstanceId = context.InstanceId,
                RawText = deal.Comments,
                CreatedAt = deal.CreatedAtUtc,
                CollectedAt = now,
                ProcessedAt = now
            };
            card = new CrmCandidateCardEntity
            {
                Id = Guid.NewGuid(),
                ResponseId = response.Id,
                OfficeId = context.OfficeId,
                Stage = deal.StageName,
                ManagerUserId = managerUserId,
                IsInActiveLoad = true,
                CreatedAtUtc = deal.CreatedAtUtc,
                UpdatedAtUtc = now,
                StageChangedAtUtc = deal.UpdatedAtUtc
            };
            db.CandidatePersons.Add(person);
            db.CandidateResponses.Add(response);
            db.CrmCandidateCards.Add(card);
            db.CandidateContactPhones.Add(new CandidateContactPhoneEntity
            {
                Id = Guid.NewGuid(),
                PersonId = person.Id,
                PhoneRaw = phoneRaw,
                PhoneNormalized = phoneNormalized,
                IsPrimary = true,
                CreatedAtUtc = deal.CreatedAtUtc,
                CreatedByUserId = actorUserId
            });
            db.CandidatePhoneHistory.Add(new CandidatePhoneHistoryEntity
            {
                Id = Guid.NewGuid(),
                PersonId = person.Id,
                ResponseId = response.Id,
                PhoneRaw = phoneRaw,
                PhoneNormalized = phoneNormalized,
                RecordedAtUtc = deal.CreatedAtUtc
            });
        }

        card.Stage = deal.StageName;
        card.ManagerUserId = managerUserId ?? card.ManagerUserId;
        card.StageChangedAtUtc = deal.UpdatedAtUtc;
        card.UpdatedAtUtc = now;
        response.BitrixContactId = deal.Contact?.Id.ToString(CultureInfo.InvariantCulture) ?? response.BitrixContactId;
        response.ProcessedAt = now;

        var existingCommentIds = await LoadImportedIdsAsync(
            card.Id,
            "NoteUpdated",
            "bitrix-comment:",
            ct);
        foreach (var comment in deal.TimelineComments.Where(x => !existingCommentIds.Contains(x.Id)))
        {
            var author = ResolveActor(comment.AuthorId, context.Snapshot.Users, managerMatches);
            db.CrmCandidateNotes.Add(new CrmCandidateNoteEntity
            {
                Id = Guid.NewGuid(),
                CardId = card.Id,
                AuthorUserId = author.UserId,
                AuthorName = author.Name,
                Text = Clamp(CleanText(comment.Text), 4000),
                CreatedAtUtc = comment.CreatedAtUtc
            });
            AddImportMarker(card.Id, "NoteUpdated", "bitrix-comment:", comment.Id, actorUserId, now);
        }

        var existingActivityIds = await LoadImportedIdsAsync(
            card.Id,
            "TaskUpdated",
            "bitrix-activity:",
            ct);
        foreach (var activity in deal.Activities.Where(x => !existingActivityIds.Contains(x.Id)))
        {
            var responsible = ResolveActor(activity.ResponsibleId, context.Snapshot.Users, managerMatches);
            var author = ResolveActor(activity.AuthorId, context.Snapshot.Users, managerMatches);
            var assignee = responsible.UserId == "bitrix"
                ? card.ManagerUserId ?? actorUserId
                : responsible.UserId;
            var task = new CrmTaskEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = context.OfficeId,
                CardId = card.Id,
                Title = Clamp(CleanText(activity.Subject), 500),
                Description = string.IsNullOrWhiteSpace(activity.Description)
                    ? null
                    : Clamp(CleanText(activity.Description), 4000),
                AssigneeUserId = assignee,
                CreatorUserId = author.UserId == "bitrix" ? actorUserId : author.UserId,
                CreatorName = author.Name,
                DueAtUtc = activity.DueAtUtc,
                Importance = CrmTaskImportances.Medium,
                TaskType = CrmTaskTypes.Contact,
                Status = activity.IsCompleted ? CrmTaskStatuses.Completed : CrmTaskStatuses.Open,
                ReminderVersion = Guid.NewGuid(),
                ReminderVersionChangedAtUtc = now,
                CreatedAtUtc = activity.CreatedAtUtc,
                UpdatedAtUtc = activity.UpdatedAtUtc,
                CompletedAtUtc = activity.CompletedAtUtc
            };
            db.CrmTasks.Add(task);
            AddImportMarker(card.Id, "TaskUpdated", "bitrix-activity:", activity.Id, actorUserId, now);
        }

        var nextDueAt = deal.Activities
            .Where(x => !x.IsCompleted && x.DueAtUtc is not null)
            .Select(x => x.DueAtUtc)
            .OrderBy(x => x)
            .FirstOrDefault();
        if (nextDueAt is not null)
        {
            card.NextActionAtUtc = nextDueAt;
        }

        if (!await db.ResponseBitrixDeliveries.AnyAsync(
            x => x.ResponseId == response.Id
                 && x.BitrixInstanceId == context.InstanceId
                 && x.BitrixEntityId == deal.Id.ToString(CultureInfo.InvariantCulture),
            ct))
        {
            db.ResponseBitrixDeliveries.Add(new ResponseBitrixDeliveryEntity
            {
                Id = Guid.NewGuid(),
                ResponseId = response.Id,
                BitrixInstanceId = context.InstanceId,
                Outcome = ResponseBitrixDeliveryOutcomes.Sent,
                BitrixEntityId = deal.Id.ToString(CultureInfo.InvariantCulture),
                BitrixEntityType = "DEAL",
                BitrixContactId = deal.Contact?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Source = "Import",
                CreatedAtUtc = now
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return card.Id;
    }

    private async Task<BitrixCrmImportPreviewDto> BuildPreviewAsync(
        ImportContext context,
        CancellationToken ct)
    {
        var managers = await LoadOrbitaManagersAsync(context.OfficeId, ct);
        var managerMatches = BuildManagerMatches(context.Snapshot.Users, managers);
        var dealIds = context.Snapshot.Deals.Select(x => x.Id.ToString(CultureInfo.InvariantCulture)).ToList();
        var importedRows = await db.CandidateResponses.AsNoTracking()
            .Where(x => x.BitrixInstanceId == context.InstanceId && dealIds.Contains(x.BitrixEntityId))
            .Join(
                db.CrmCandidateCards.AsNoTracking(),
                response => response.Id,
                card => card.ResponseId,
                (response, card) => new { response.BitrixEntityId, CardId = card.Id })
            .ToListAsync(ct);
        var imported = importedRows
            .GroupBy(x => x.BitrixEntityId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().CardId, StringComparer.Ordinal);
        var normalizedPhones = context.Snapshot.Deals
            .Select(x => phoneNormalizer.Normalize(x.Contact?.Phone ?? string.Empty))
            .Where(x => x.Length > 0)
            .Distinct()
            .ToList();
        var existingByPhone = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => x.OfficeId == context.OfficeId && normalizedPhones.Contains(x.Response.PhoneNormalized))
            .Select(x => new { x.Id, x.Response.PhoneNormalized })
            .ToListAsync(ct);
        var phoneCards = existingByPhone
            .GroupBy(x => x.PhoneNormalized, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.Ordinal);

        var deals = new List<BitrixCrmImportDealPreviewDto>();
        foreach (var deal in context.Snapshot.Deals)
        {
            var phone = deal.Contact?.Phone.Trim() ?? string.Empty;
            var normalized = phoneNormalizer.Normalize(phone);
            var bitrixId = deal.Id.ToString(CultureInfo.InvariantCulture);
            imported.TryGetValue(bitrixId, out var importedCardId);
            phoneCards.TryGetValue(normalized, out var phoneCardId);
            var responsible = deal.ResponsibleId is long responsibleId
                              && context.Snapshot.Users.TryGetValue(responsibleId, out var user)
                ? user.FullName
                : deal.ResponsibleId is long fallbackId ? $"Bitrix ID {fallbackId}" : string.Empty;
            var match = deal.ResponsibleId is long responsibleUserId
                        && managerMatches.TryGetValue(responsibleUserId, out var matched)
                ? matched
                : null;
            var action = importedCardId != Guid.Empty
                ? BitrixCrmImportActions.AlreadyImported
                : normalized.Length < 10
                    ? BitrixCrmImportActions.MissingPhone
                    : phoneCardId != Guid.Empty
                        ? BitrixCrmImportActions.UpdateExisting
                        : BitrixCrmImportActions.Create;
            var warning = action == BitrixCrmImportActions.MissingPhone
                ? "У сделки нет телефона в связанном контакте."
                : deal.ResponsibleId is not null && match is null
                    ? "Ответственный не сопоставлен с сотрудником Орбиты."
                    : null;
            deals.Add(new BitrixCrmImportDealPreviewDto(
                deal.Id,
                ResolveFullName(deal),
                phone,
                ResolveField(deal, context.Snapshot.DealFieldTitles, ["город"]),
                ResolveField(deal, context.Snapshot.DealFieldTitles, ["вакансия", "профессия"]),
                deal.StageName,
                deal.ResponsibleId,
                responsible,
                match?.OrbitaUserId,
                match?.OrbitaName,
                deal.TimelineComments.Count,
                deal.Activities.Count,
                action,
                importedCardId != Guid.Empty ? importedCardId : phoneCardId != Guid.Empty ? phoneCardId : null,
                warning));
        }

        return new BitrixCrmImportPreviewDto(
            context.InstanceId,
            context.OfficeId,
            context.PortalHost,
            context.CategoryId,
            context.Snapshot.Stages.Select(stage => new BitrixCrmImportStageSummaryDto(
                stage.Name,
                stage.Id,
                context.Snapshot.Deals.Count(deal =>
                    string.Equals(deal.StageId, stage.Id, StringComparison.OrdinalIgnoreCase)))).ToList(),
            managerMatches.Values
                .OrderBy(x => x.BitrixName, StringComparer.OrdinalIgnoreCase)
                .Select(x => new BitrixCrmImportManagerMatchDto(
                    x.BitrixUserId,
                    x.BitrixName,
                    x.OrbitaUserId,
                    x.OrbitaName,
                    x.OrbitaUserId is not null))
                .ToList(),
            deals,
            deals.Count(x => x.Action == BitrixCrmImportActions.Create),
            deals.Count(x => x.Action == BitrixCrmImportActions.UpdateExisting),
            deals.Count(x => x.Action == BitrixCrmImportActions.AlreadyImported),
            deals.Count(x => x.Action == BitrixCrmImportActions.MissingPhone));
    }

    private async Task<ImportContext> LoadContextAsync(
        Guid bitrixInstanceId,
        OfficeScope scope,
        Guid? requestedOfficeId,
        int categoryId,
        IReadOnlyList<string>? requestedStages,
        IReadOnlyCollection<long>? dealIds,
        CancellationToken ct)
    {
        var instance = await db.BitrixInstances.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == bitrixInstanceId && x.DeletedAtUtc == null, ct);
        if (instance is null
            || !scope.HasAccess
            || !scope.CanAccessOffice(instance.OfficeId)
            || (requestedOfficeId is Guid officeFilter && officeFilter != instance.OfficeId))
        {
            return ImportContext.Failed("Интеграция Bitrix24 не найдена или недоступна.");
        }

        if (!instance.IsEnabled)
        {
            return ImportContext.Failed("Интеграция Bitrix24 отключена.");
        }

        var office = await db.Offices.AsNoTracking()
            .Where(x => x.Id == instance.OfficeId && x.IsEnabled && x.CrmEnabled)
            .Select(x => new { x.CrmStagesJson })
            .FirstOrDefaultAsync(ct);
        if (office is null)
        {
            return ImportContext.Failed("CRM выбранного офиса отключена.");
        }

        var stages = (requestedStages is { Count: > 0 } ? requestedStages : BitrixCrmImportStages.Default)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (stages.Count == 0)
        {
            return ImportContext.Failed("Не выбраны стадии для импорта.");
        }

        var officeStages = CrmStages.Resolve(office.CrmStagesJson);
        var missingOfficeStages = stages
            .Where(stage => !CrmStages.Contains(officeStages, stage))
            .ToList();
        if (missingOfficeStages.Count > 0)
        {
            return ImportContext.Failed(
                "В воронке Орбиты отсутствуют этапы: " + string.Join(", ", missingOfficeStages));
        }

        var webhookUrl = await bitrixInstances.ResolveWebhookUrlAsync(instance, ct);
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return ImportContext.Failed("Для Bitrix24 не настроен входящий вебхук.");
        }

        try
        {
            var snapshot = await client.LoadAsync(webhookUrl, categoryId, stages, dealIds, ct);
            snapshot = NormalizeStageNames(snapshot, officeStages);
            if (snapshot.Deals.Count > MaxImportDeals)
            {
                return ImportContext.Failed(
                    $"Найдено {snapshot.Deals.Count} сделок. Максимум за один импорт: {MaxImportDeals}.");
            }

            return new ImportContext(
                instance.Id,
                instance.OfficeId,
                instance.PortalHost ?? new Uri(webhookUrl).Host,
                categoryId,
                snapshot,
                null);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return ImportContext.Failed(ex.Message);
        }
    }

    private async Task<List<OrbitaManager>> LoadOrbitaManagersAsync(Guid officeId, CancellationToken ct)
    {
        var profiles = await db.PanelUserProfiles.AsNoTracking()
            .Where(x => x.OfficeId == officeId && x.FullName != null && x.FullName != string.Empty)
            .Select(x => new { x.UserId, x.FullName })
            .ToListAsync(ct);
        return profiles
            .Select(x => new OrbitaManager(x.UserId, x.FullName!.Trim(), NormalizeName(x.FullName)))
            .ToList();
    }

    private static IReadOnlyDictionary<long, ManagerMatch> BuildManagerMatches(
        IReadOnlyDictionary<long, BitrixImportUser> users,
        IReadOnlyList<OrbitaManager> managers)
    {
        var result = new Dictionary<long, ManagerMatch>();
        foreach (var user in users.Values)
        {
            var normalized = NormalizeName(user.FullName);
            var matches = managers.Where(x => x.NormalizedName == normalized).ToList();
            if (matches.Count == 0)
            {
                matches = managers
                    .Where(x => NamesMatchByUniqueKnownParts(normalized, x.NormalizedName))
                    .ToList();
            }

            if (matches.Count == 1)
            {
                result[user.Id] = new ManagerMatch(
                    user.Id,
                    user.FullName,
                    matches[0].UserId,
                    matches[0].FullName);
            }
            else
            {
                result[user.Id] = new ManagerMatch(user.Id, user.FullName, null, null);
            }
        }

        return result;
    }

    private async Task<HashSet<long>> LoadImportedIdsAsync(
        Guid cardId,
        string action,
        string prefix,
        CancellationToken ct)
    {
        var values = await db.CrmCandidateHistory.AsNoTracking()
            .Where(x => x.CardId == cardId && x.Action == action && x.Details != null)
            .Select(x => x.Details!)
            .ToListAsync(ct);
        return values
            .Select(x => x.StartsWith(prefix, StringComparison.Ordinal)
                && long.TryParse(x[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                    ? id
                    : 0)
            .Where(x => x > 0)
            .ToHashSet();
    }

    private void AddImportMarker(
        Guid cardId,
        string action,
        string prefix,
        long externalId,
        string actorUserId,
        DateTime now) =>
        db.CrmCandidateHistory.Add(new CrmCandidateHistoryEntity
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            Action = action,
            Details = prefix + externalId.ToString(CultureInfo.InvariantCulture),
            ActorUserId = actorUserId,
            ActorName = "Импорт Bitrix24",
            CreatedAtUtc = now
        });

    private static Actor ResolveActor(
        long? bitrixUserId,
        IReadOnlyDictionary<long, BitrixImportUser> users,
        IReadOnlyDictionary<long, ManagerMatch> matches)
    {
        if (bitrixUserId is long id && matches.TryGetValue(id, out var match) && match.OrbitaUserId is not null)
        {
            return new Actor(match.OrbitaUserId, match.OrbitaName ?? match.BitrixName);
        }

        if (bitrixUserId is long fallbackId && users.TryGetValue(fallbackId, out var user))
        {
            return new Actor("bitrix", user.FullName);
        }

        return new Actor("bitrix", "Bitrix24");
    }

    private static string ResolveFullName(BitrixImportDeal deal) =>
        FirstNonEmpty(deal.Contact?.FullName ?? string.Empty, deal.Title, $"Кандидат {deal.Id}");

    private static int? ResolveAge(
        BitrixImportDeal deal,
        IReadOnlyDictionary<string, string> fieldTitles)
    {
        var value = ResolveField(deal, fieldTitles, ["возраст"]);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var age)
               && age is >= 14 and <= 99
            ? age
            : null;
    }

    private static string ResolveField(
        BitrixImportDeal deal,
        IReadOnlyDictionary<string, string> fieldTitles,
        IReadOnlyCollection<string> requestedTitles)
    {
        foreach (var field in deal.Fields)
        {
            if (!fieldTitles.TryGetValue(field.Key, out var title)
                || !requestedTitles.Any(requested =>
                    string.Equals(title.Trim(), requested, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(field.Value))
            {
                return field.Value.Trim();
            }
        }

        var text = deal.Comments;
        foreach (var requested in requestedTitles)
        {
            var match = Regex.Match(
                text ?? string.Empty,
                $@"(?im)^\s*{Regex.Escape(requested)}\s*:\s*(.+?)\s*$",
                RegexOptions.CultureInvariant);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
        }

        return string.Empty;
    }

    private static string BuildSourceResponseId(string portalHost, long dealId)
    {
        var value = $"bitrix:{portalHost.ToLowerInvariant()}:{dealId}";
        return value.Length <= 128 ? value : value[..128];
    }

    private static string CleanText(string value)
    {
        var withoutTags = HtmlTagRegex.Replace(value ?? string.Empty, " ");
        var withoutBbCode = BbCodeTagRegex.Replace(withoutTags, " ");
        return WhiteSpaceRegex.Replace(WebUtility.HtmlDecode(withoutBbCode), " ").Trim();
    }

    private static BitrixImportSnapshot NormalizeStageNames(
        BitrixImportSnapshot snapshot,
        IReadOnlyList<string> officeStages)
    {
        string Resolve(string value) =>
            officeStages.FirstOrDefault(stage =>
                string.Equals(stage, value.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? value.Trim();

        return snapshot with
        {
            Stages = snapshot.Stages
                .Select(stage => stage with { Name = Resolve(stage.Name) })
                .ToList(),
            Deals = snapshot.Deals
                .Select(deal => deal with { StageName = Resolve(deal.StageName) })
                .ToList()
        };
    }

    private static string Clamp(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string NormalizeName(string? value) =>
        WhiteSpaceRegex.Replace((value ?? string.Empty).Trim().ToLowerInvariant().Replace('ё', 'е'), " ");

    private static bool NamesMatchByUniqueKnownParts(string left, string right)
    {
        var leftParts = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightParts = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var shorter = leftParts.Count <= rightParts.Count ? leftParts : rightParts;
        var longer = ReferenceEquals(shorter, leftParts) ? rightParts : leftParts;

        // One-word names remain strict: matching "Dmitry" to any longer name is unsafe.
        return shorter.Count >= 2 && shorter.IsSubsetOf(longer);
    }

    private sealed record ImportContext(
        Guid InstanceId,
        Guid OfficeId,
        string PortalHost,
        int CategoryId,
        BitrixImportSnapshot Snapshot,
        string? Error)
    {
        public static ImportContext Failed(string error) =>
            new(Guid.Empty, Guid.Empty, string.Empty, 0, new BitrixImportSnapshot([], new Dictionary<string, string>(), new Dictionary<long, BitrixImportUser>(), []), error);
    }

    private sealed record OrbitaManager(string UserId, string FullName, string NormalizedName);
    private sealed record ManagerMatch(long BitrixUserId, string BitrixName, string? OrbitaUserId, string? OrbitaName);
    private sealed record Actor(string UserId, string Name);
}
