using System.Globalization;
using Orbita.Contracts;
using Orbita.Web.Formatting;
using Orbita.Web.Models.ViewModels;

namespace Orbita.Web.Services;

internal static class DesignPreviewData
{
    public static readonly Guid WorkerMoscowId = Guid.Parse("11111111-1111-1111-1111-111111111101");
    public static readonly Guid WorkerSpbId = Guid.Parse("11111111-1111-1111-1111-111111111102");
    public static readonly Guid WorkerKazanId = Guid.Parse("11111111-1111-1111-1111-111111111103");

    private static readonly Guid[] PreviewWorkerIds =
    [
        WorkerMoscowId,
        WorkerSpbId,
        WorkerKazanId,
        Guid.Parse("11111111-1111-1111-1111-111111111104"),
        Guid.Parse("11111111-1111-1111-1111-111111111105"),
        Guid.Parse("11111111-1111-1111-1111-111111111106"),
        Guid.Parse("11111111-1111-1111-1111-111111111107"),
        Guid.Parse("11111111-1111-1111-1111-111111111108"),
        Guid.Parse("11111111-1111-1111-1111-111111111109"),
        Guid.Parse("11111111-1111-1111-1111-111111111110"),
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("11111111-1111-1111-1111-111111111112")
    ];

    public static readonly Guid AccountAlphaId = Guid.Parse("22222222-2222-2222-2222-222222222201");
    public static readonly Guid AccountBetaId = Guid.Parse("22222222-2222-2222-2222-222222222202");
    public static readonly Guid AccountGammaId = Guid.Parse("22222222-2222-2222-2222-222222222203");
    public static readonly Guid PreviewOfficeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid PreviewOffice2Id = Guid.Parse("22222222-2222-2222-2222-222222222201");

    private static readonly DateTime Now = DateTime.UtcNow;

    // CRM preview intentionally lives only in memory. It lets designers click through the
    // manager workflow without starting the API or PostgreSQL.
    private static readonly object CrmSync = new();
    private const string PreviewManagerElena = "preview-manager-elena";
    private const string PreviewManagerIgor = "preview-manager-igor";
    private static bool _previewCrmShiftActive = true;
    private static bool _previewCrmEnabled = true;
    private static bool _previewCrmDeadlineNotificationsEnabled = true;
    private static List<string> _previewCrmStages = CrmStages.Default.ToList();
    private static readonly List<PreviewCrmCandidate> PreviewCrmCandidates =
    [
        new(Guid.Parse("90000000-0000-0000-0000-000000000001"), "Селезнёв Артур Алексеевич", 55, "+7 912 445-18-07", "Тында", "Разнорабочий на вахту", CrmStages.Lead, PreviewManagerElena, true, 35),
        new(Guid.Parse("90000000-0000-0000-0000-000000000002"), "Ахмадалиев Сабиржон Садриддинович", 62, "+7 900 201-74-65", "Бородино", "Сварщик на вахту с проживанием", CrmStages.Lead, PreviewManagerIgor, true, 70),
        new(Guid.Parse("90000000-0000-0000-0000-000000000003"), "Турунцев Сергей Леонидович", 42, "+7 982 133-05-91", "Киров", "Электрик вахта с питанием", CrmStages.Ndz73, PreviewManagerElena, true, 105),
        new(Guid.Parse("90000000-0000-0000-0000-000000000004"), "Цветков Сергей Андреевич", 30, "+7 950 784-12-20", "Сыктывкар", "Слесарь на вахту", CrmStages.Ndz26, PreviewManagerIgor, true, 150),
        new(Guid.Parse("90000000-0000-0000-0000-000000000005"), "Василий Демичев", 41, "+7 917 332-48-09", "Тихвин", "Охранник вахта с питанием", CrmStages.Substitution, PreviewManagerElena, true, 190),
        new(Guid.Parse("90000000-0000-0000-0000-000000000006"), "Магомедов Либир Алигадыджиевич", 61, "+7 964 285-61-14", "Махачкала", "Сварщик", CrmStages.Negotiations, PreviewManagerIgor, true, 240),
        new(Guid.Parse("90000000-0000-0000-0000-000000000007"), "Махмутов Марат Магсумович", 49, "+7 908 447-93-52", "Анастасово", "Охранник вахта", CrmStages.Questionnaire, PreviewManagerElena, true, 285),
        new(Guid.Parse("90000000-0000-0000-0000-000000000008"), "Гаджиев Руслан Сулейманович", 35, "+7 995 623-40-15", "Хасавюрт", "Слесарь на вахту", CrmStages.Ticket, PreviewManagerIgor, false, 340),
        new(Guid.Parse("90000000-0000-0000-0000-000000000009"), "Алексей Корнев", 28, "+7 927 104-70-32", "Самара", "Комплектовщик на склад", CrmStages.Lead, null, false, 12),
        new(Guid.Parse("90000000-0000-0000-0000-000000000010"), "Тестовая карточка 01", 31, "+7 900 000-00-01", "Пермь", "Слесарь на вахту", CrmStages.Lead, PreviewManagerElena, true, 18),
        new(Guid.Parse("90000000-0000-0000-0000-000000000011"), "Тестовая карточка 02", 36, "+7 900 000-00-02", "Омск", "Разнорабочий с проживанием", CrmStages.Lead, PreviewManagerIgor, true, 24),
        new(Guid.Parse("90000000-0000-0000-0000-000000000012"), "Тестовая карточка 03", 43, "+7 900 000-00-03", "Казань", "Электрик", CrmStages.Lead, PreviewManagerElena, true, 32),
        new(Guid.Parse("90000000-0000-0000-0000-000000000013"), "Тестовая карточка 04", 29, "+7 900 000-00-04", "Уфа", "Сварщик", CrmStages.Lead, PreviewManagerIgor, true, 41),
        new(Guid.Parse("90000000-0000-0000-0000-000000000014"), "Тестовая карточка 05", 47, "+7 900 000-00-05", "Тюмень", "Охранник на вахту", CrmStages.Lead, PreviewManagerElena, true, 53),
        new(Guid.Parse("90000000-0000-0000-0000-000000000015"), "Тестовая карточка 06", 38, "+7 900 000-00-06", "Ижевск", "Комплектовщик", CrmStages.Lead, PreviewManagerIgor, true, 67),
        new(Guid.Parse("90000000-0000-0000-0000-000000000016"), "Тестовая карточка 07", 52, "+7 900 000-00-07", "Киров", "Машинист", CrmStages.Lead, PreviewManagerElena, true, 79),
        new(Guid.Parse("90000000-0000-0000-0000-000000000017"), "Тестовая карточка 08", 34, "+7 900 000-00-08", "Самара", "Водитель категории C", CrmStages.Lead, PreviewManagerIgor, true, 88),
        new(Guid.Parse("90000000-0000-0000-0000-000000000018"), "Тестовая карточка 09", 41, "+7 900 000-00-09", "Челябинск", "Монтажник", CrmStages.Lead, PreviewManagerElena, true, 96),
        new(Guid.Parse("90000000-0000-0000-0000-000000000019"), "Тестовая карточка 10", 45, "+7 900 000-00-10", "Екатеринбург", "Стропальщик", CrmStages.Lead, PreviewManagerIgor, true, 110),
        new(Guid.Parse("90000000-0000-0000-0000-000000000020"), "Тестовая карточка 11", 27, "+7 900 000-00-11", "Барнаул", "Кладовщик", CrmStages.Lead, PreviewManagerElena, true, 124),
        new(Guid.Parse("90000000-0000-0000-0000-000000000021"), "Тестовая карточка 12", 49, "+7 900 000-00-12", "Новосибирск", "Бетонщик", CrmStages.Lead, PreviewManagerIgor, true, 139)
    ];
    private static readonly Dictionary<Guid, List<CrmNoteDto>> PreviewCrmNotes = new()
    {
        [Guid.Parse("90000000-0000-0000-0000-000000000001")] =
        [
            new(Guid.Parse("91000000-0000-0000-0000-000000000001"), PreviewManagerElena, "Елена Воронцова", "Созвониться после 18:00, кандидат сейчас на работе.", Now.AddMinutes(-28), CanEdit: true, CanDelete: true, CanPin: true)
        ]
    };
    private static readonly List<CrmTaskDto> PreviewCrmTasks =
    [
        new(Guid.Parse("92000000-0000-0000-0000-000000000001"), Guid.Parse("90000000-0000-0000-0000-000000000001"), "Селезнёв Артур Алексеевич", "Уточнить дату выезда", "Попросить прислать фото паспорта в мессенджер.", PreviewManagerElena, "Елена Воронцова", "preview-admin", "Администратор", Now.AddHours(3), CrmTaskStatuses.Open, Now.AddHours(-1), null, false),
        new(Guid.Parse("92000000-0000-0000-0000-000000000002"), Guid.Parse("90000000-0000-0000-0000-000000000003"), "Турунцев Сергей Леонидович", "Перезвонить", null, PreviewManagerElena, "Елена Воронцова", PreviewManagerElena, "Елена Воронцова", Now.AddHours(-2), CrmTaskStatuses.Open, Now.AddHours(-5), null, true),
        new(Guid.Parse("92000000-0000-0000-0000-000000000003"), null, null, "Проверить вакансии на неделю", null, PreviewManagerElena, "Елена Воронцова", PreviewManagerElena, "Елена Воронцова", Now.AddDays(1), CrmTaskStatuses.Open, Now.AddHours(-8), null, false)
    ];
    private static readonly Dictionary<Guid, List<CrmTaskCommentDto>> PreviewCrmTaskComments = new()
    {
        [Guid.Parse("92000000-0000-0000-0000-000000000001")] =
        [
            new(Guid.Parse("92500000-0000-0000-0000-000000000001"), Guid.Parse("92000000-0000-0000-0000-000000000001"), PreviewManagerElena, "Елена Воронцова", "Кандидат обещал прислать документы после смены.", Now.AddMinutes(-35), CanEdit: true, CanDelete: true)
        ]
    };
    private static readonly Dictionary<Guid, List<CrmTaskAttachmentDto>> PreviewCrmTaskAttachments = new()
    {
        [Guid.Parse("92000000-0000-0000-0000-000000000001")] =
        [
            new(Guid.Parse("92600000-0000-0000-0000-000000000001"), "Документы кандидата.pdf", "application/pdf", 183_500, "Елена Воронцова", Now.AddMinutes(-31))
        ]
    };
    private static readonly Dictionary<Guid, byte[]> PreviewCrmTaskAttachmentContent = new()
    {
        [Guid.Parse("92600000-0000-0000-0000-000000000001")] = "Предпросмотр вложения к задаче."u8.ToArray()
    };
    private static readonly List<CrmTaskNotificationDto> PreviewCrmTaskNotifications =
    [
        new(
            Guid.Parse("92700000-0000-0000-0000-000000000001"),
            Guid.Parse("92000000-0000-0000-0000-000000000002"),
            Guid.Parse("90000000-0000-0000-0000-000000000003"),
            CrmTaskNotificationKinds.Overdue,
            "Перезвонить кандидату",
            "Срок задачи истёк. Свяжитесь с кандидатом и обновите результат.",
            Now.AddHours(-2),
            Now.AddMinutes(-8),
            null),
        new(
            Guid.Parse("92700000-0000-0000-0000-000000000002"),
            Guid.Parse("92000000-0000-0000-0000-000000000001"),
            Guid.Parse("90000000-0000-0000-0000-000000000001"),
            CrmTaskNotificationKinds.DueIn24Hours,
            "Уточнить дату выезда",
            "До срока задачи осталось меньше 24 часов.",
            Now.AddHours(3),
            Now.AddMinutes(-18),
            null),
        new(
            Guid.Parse("92700000-0000-0000-0000-000000000003"),
            Guid.Parse("92000000-0000-0000-0000-000000000003"),
            null,
            CrmTaskNotificationKinds.DueIn24Hours,
            "Проверить вакансии на неделю",
            "До срока задачи осталось 24 часа.",
            Now.AddDays(1),
            Now.AddHours(-1),
            Now.AddMinutes(-30))
    ];
    private static readonly List<CrmHistoryDto> PreviewCrmHistory =
    [
        new(Guid.Parse("93000000-0000-0000-0000-000000000001"), "Created", "Отклик из Avito", "system", "Система", Now.AddMinutes(-35)),
        new(Guid.Parse("93000000-0000-0000-0000-000000000002"), "Assigned", "Елена Воронцова", "preview-admin", "Администратор", Now.AddMinutes(-34))
    ];

    public static CrmBoardDto GetCrmBoard(CrmBoardQuery? query = null)
    {
        lock (CrmSync)
        {
            query ??= new CrmBoardQuery();
            var managers = BuildPreviewCrmManagers();
            var boardView = CrmBoardViews.Normalize(query.View);
            var pageSize = CrmBoardListOptions.NormalizePageSize(query.PageSize);
            var requestedPage = CrmBoardListOptions.NormalizePage(query.Page);
            var sort = CrmBoardSorts.Normalize(query.Sort);
            var sortDir = CrmBoardSorts.NormalizeDirection(query.SortDir);
            var scope = query.Scope switch
            {
                CrmBoardScopes.Mine => CrmBoardScopes.Mine,
                CrmBoardScopes.Team => CrmBoardScopes.Team,
                CrmBoardScopes.Unassigned => CrmBoardScopes.Unassigned,
                CrmBoardScopes.Closed => CrmBoardScopes.Closed,
                _ => CrmBoardScopes.Team
            };
            var selectedManagerUserId = scope == CrmBoardScopes.Team
                                        || scope == CrmBoardScopes.Closed
                ? !string.IsNullOrWhiteSpace(query.ManagerUserId)
                  && managers.Any(x => string.Equals(
                      x.UserId,
                      query.ManagerUserId.Trim(),
                      StringComparison.Ordinal))
                    ? query.ManagerUserId.Trim()
                    : null
                : null;
            var cards = PreviewCrmCandidates.AsEnumerable();
            var hasSearch = !string.IsNullOrWhiteSpace(query.Search);
            var includeClosed = query.IncludeClosed || hasSearch;
            if (hasSearch)
            {
                var term = query.Search!.Trim();
                var digits = SearchQueryNormalizer.ExtractDigits(term);
                var normalizedPhoneDigits = digits.Length == 11 && digits.StartsWith('8')
                    ? $"7{digits[1..]}"
                    : digits;
                var searchByPhone = digits.Length >= 4;
                cards = cards.Where(c =>
                    c.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    c.City.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    c.Vacancy.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    c.PhoneRaw.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (searchByPhone && NormalizePreviewPhone(c.PhoneRaw).Contains(normalizedPhoneDigits, StringComparison.Ordinal)));
            }

            if (!string.IsNullOrWhiteSpace(query.City))
            {
                cards = cards.Where(c => c.City.Contains(query.City, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query.Vacancy))
            {
                cards = cards.Where(c => c.Vacancy.Contains(query.Vacancy, StringComparison.OrdinalIgnoreCase));
            }

            if (query.ActiveLoadOnly)
            {
                cards = cards.Where(c => c.IsInActiveLoad);
            }

            if (scope == CrmBoardScopes.Unassigned)
            {
                cards = cards.Where(c => c.ManagerUserId is null && (!c.IsClosed || includeClosed));
            }
            else if (scope == CrmBoardScopes.Closed)
            {
                cards = cards.Where(c => c.IsClosed);
                if (selectedManagerUserId is not null)
                {
                    cards = cards.Where(c => c.ManagerUserId == selectedManagerUserId);
                }
            }
            else if (scope == CrmBoardScopes.Mine)
            {
                cards = cards.Where(c => c.ManagerUserId == PreviewManagerElena && (!c.IsClosed || includeClosed));
            }
            else if (selectedManagerUserId is not null)
            {
                cards = cards.Where(c => c.ManagerUserId == selectedManagerUserId && (!c.IsClosed || includeClosed));
            }
            else if (!includeClosed)
            {
                cards = cards.Where(c => !c.IsClosed);
            }

            var selectedCloseReason = scope == CrmBoardScopes.Closed
                                      && CrmCloseReasons.IsValid(query.CloseReason)
                ? query.CloseReason!.Trim()
                : null;
            if (selectedCloseReason is not null)
            {
                cards = cards.Where(c => string.Equals(c.CloseReason, selectedCloseReason, StringComparison.Ordinal));
            }

            if (query.OverdueOnly)
            {
                cards = cards.Where(c =>
                    c.NextActionAtUtc is DateTime next && next < DateTime.UtcNow
                    || PreviewCrmTasks.Any(task =>
                        task.CardId == c.Id
                        && task.Status == CrmTaskStatuses.Open
                        && task.IsOverdue));
            }

            var filtered = cards.ToList();
            var totalItems = filtered.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
            var page = boardView == CrmBoardViews.List
                ? Math.Min(requestedPage, totalPages)
                : 1;
            IEnumerable<PreviewCrmCandidate> ordered = (sort, sortDir) switch
            {
                (CrmBoardSorts.Candidate, "asc") => filtered.OrderBy(c => c.FullName).ThenBy(c => c.Id),
                (CrmBoardSorts.Candidate, _) => filtered.OrderByDescending(c => c.FullName).ThenByDescending(c => c.Id),
                (CrmBoardSorts.Phone, "asc") => filtered.OrderBy(c => c.PhoneRaw).ThenBy(c => c.Id),
                (CrmBoardSorts.Phone, _) => filtered.OrderByDescending(c => c.PhoneRaw).ThenByDescending(c => c.Id),
                (CrmBoardSorts.Vacancy, "asc") => filtered.OrderBy(c => c.Vacancy).ThenBy(c => c.Id),
                (CrmBoardSorts.Vacancy, _) => filtered.OrderByDescending(c => c.Vacancy).ThenByDescending(c => c.Id),
                (CrmBoardSorts.Stage, "asc") => filtered.OrderBy(c => c.Stage).ThenBy(c => c.Id),
                (CrmBoardSorts.Stage, _) => filtered.OrderByDescending(c => c.Stage).ThenByDescending(c => c.Id),
                (CrmBoardSorts.Manager, "asc") => filtered.OrderBy(c => c.ManagerUserId).ThenBy(c => c.Id),
                (CrmBoardSorts.Manager, _) => filtered.OrderByDescending(c => c.ManagerUserId).ThenByDescending(c => c.Id),
                (CrmBoardSorts.Changed, "asc") => filtered.OrderBy(c => c.StageChangedAtUtc).ThenBy(c => c.Id),
                (CrmBoardSorts.Changed, _) => filtered.OrderByDescending(c => c.StageChangedAtUtc).ThenByDescending(c => c.Id),
                (CrmBoardSorts.Created, "asc") => filtered.OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id),
                _ => filtered.OrderByDescending(c => c.CreatedAtUtc).ThenByDescending(c => c.Id)
            };
            var list = boardView == CrmBoardViews.List
                ? ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList()
                : filtered;
            var stages = _previewCrmStages
                .Select(stage =>
                {
                    var stageCards = list.Where(c => c.Stage == stage && !c.IsClosed)
                        .OrderByDescending(c => c.CreatedAtUtc)
                        .Select(ToPreviewCrmCard)
                        .ToList();
                    return new CrmStageDto(stage, stageCards, stageCards.Count);
                })
                .ToList();
            if (scope == CrmBoardScopes.Closed || includeClosed)
            {
                var closedCards = list
                    .Where(c => c.IsClosed)
                    .OrderByDescending(c => c.StageChangedAtUtc)
                    .Select(ToPreviewCrmCard)
                    .ToList();
                if (closedCards.Count > 0)
                {
                    stages.Add(new CrmStageDto("Закрыто", closedCards, closedCards.Count));
                }
            }
            var activeLoad = PreviewCrmCandidates.Count(c => c.ManagerUserId == PreviewManagerElena && c.IsInActiveLoad && !c.IsClosed);
            var openTasks = PreviewCrmTasks.Count(t => t.Status == CrmTaskStatuses.Open);
            var overdue = PreviewCrmTasks.Count(t => t.IsOverdue && t.Status == CrmTaskStatuses.Open);
            var team = new CrmTeamStatsDto(
                PreviewCrmCandidates.Count(c => !c.IsClosed),
                PreviewCrmCandidates.Count(c => c.ManagerUserId is null && !c.IsClosed),
                managers.Count(m => m.IsShiftActive),
                managers.Count,
                PreviewCrmCandidates.Count(c => c.IsClosed),
                4,
                _previewCrmStages.Select(s => new CrmStageCountDto(s, PreviewCrmCandidates.Count(c => c.Stage == s && !c.IsClosed))).ToList());
            return new CrmBoardDto(
                _previewCrmEnabled,
                true,
                _previewCrmShiftActive,
                10,
                activeLoad,
                stages,
                managers,
                team.UnassignedCount,
                openTasks,
                overdue,
                true,
                true,
                team,
                scope,
                query.Search,
                query.City,
                query.Vacancy,
                query.OverdueOnly,
                query.ActiveLoadOnly,
                query.IncludeClosed,
                _previewCrmStages.ToList(),
                _previewCrmDeadlineNotificationsEnabled,
                selectedManagerUserId,
                selectedCloseReason,
                boardView,
                page,
                pageSize,
                totalItems,
                sort,
                sortDir,
                boardView == CrmBoardViews.List ? list.Select(ToPreviewCrmCard).ToList() : null);
        }
    }

    private static string NormalizePreviewPhone(string value)
    {
        var digits = SearchQueryNormalizer.ExtractDigits(value);
        return digits.Length == 11 && digits.StartsWith('8')
            ? $"7{digits[1..]}"
            : digits;
    }

    public static CrmAnalyticsDto GetCrmAnalytics(
        Guid? officeId,
        DateTime fromUtc,
        DateTime toUtc,
        string? managerUserId = null)
    {
        var normalizedFrom = fromUtc.Kind == DateTimeKind.Utc
            ? fromUtc
            : DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        var normalizedTo = toUtc.Kind == DateTimeKind.Utc
            ? toUtc
            : DateTime.SpecifyKind(toUtc, DateTimeKind.Utc);
        if (normalizedTo <= normalizedFrom)
        {
            normalizedTo = normalizedFrom.AddDays(1);
        }

        var periodDays = Math.Clamp((normalizedTo - normalizedFrom).TotalDays, 1d, DashboardPeriod.MaxDays);
        var periodFactor = periodDays / 30d;
        int Scale(int value) => value == 0
            ? 0
            : Math.Max(1, (int)Math.Round(value * periodFactor, MidpointRounding.AwayFromZero));

        var managerBaselines = new[]
        {
            new PreviewCrmAnalyticsManager(
                PreviewOfficeId, "Основной", PreviewManagerElena, "Елена Воронцова", true,
                10, 21, 7, 57, 29, 28, 14, 8, 5, 1),
            new PreviewCrmAnalyticsManager(
                PreviewOfficeId, "Основной", PreviewManagerIgor, "Игорь Белов", true,
                10, 24, 9, 54, 24, 30, 13, 9, 6, 2),
            new PreviewCrmAnalyticsManager(
                PreviewOffice2Id, "Сибирь", "preview-manager-tatiana", "Татьяна Орлова", true,
                12, 18, 10, 32, 15, 17, 7, 7, 4, 0),
            new PreviewCrmAnalyticsManager(
                PreviewOffice2Id, "Сибирь", "preview-manager-denis", "Денис Карпов", false,
                10, 15, 5, 28, 14, 14, 5, 6, 3, 1)
        };
        var officeBaselines = new[]
        {
            new PreviewCrmAnalyticsOffice(PreviewOfficeId, "Основной", 118, 111, 55, 63, 29),
            new PreviewCrmAnalyticsOffice(PreviewOffice2Id, "Сибирь", 66, 60, 31, 35, 13)
        };

        var scopedOffices = officeBaselines
            .Where(x => officeId is null || x.OfficeId == officeId)
            .ToList();
        var managerOptions = managerBaselines
            .Where(x => officeId is null || x.OfficeId == officeId)
            .Select(x => new CrmAnalyticsManagerOptionDto(x.UserId, x.DisplayName, x.OfficeId, x.OfficeName))
            .OrderBy(x => x.OfficeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedManagers = managerBaselines
            .Where(x => (officeId is null || x.OfficeId == officeId)
                        && (string.IsNullOrWhiteSpace(managerUserId)
                            || string.Equals(x.UserId, managerUserId, StringComparison.Ordinal)))
            .ToList();

        PreviewCrmAnalyticsTotals totals;
        if (!string.IsNullOrWhiteSpace(managerUserId))
        {
            totals = selectedManagers.Aggregate(
                PreviewCrmAnalyticsTotals.Empty,
                (current, manager) => current.Add(new PreviewCrmAnalyticsTotals(
                    manager.Received,
                    manager.Received,
                    manager.Active,
                    manager.Closed,
                    manager.SuccessfulClosed)));
        }
        else
        {
            totals = scopedOffices.Aggregate(
                PreviewCrmAnalyticsTotals.Empty,
                (current, office) => current.Add(new PreviewCrmAnalyticsTotals(
                    office.Received,
                    office.Assigned,
                    office.Active,
                    office.Closed,
                    office.SuccessfulClosed)));
        }

        var received = Scale(totals.Received);
        var assigned = Math.Min(received, Scale(totals.Assigned));
        var active = Math.Min(received, Scale(totals.Active));
        var closed = Math.Max(0, received - active);
        var successful = Math.Min(closed, Scale(totals.SuccessfulClosed));
        var cards = new CrmAnalyticsCardMetricsDto(
            received,
            assigned,
            active,
            closed,
            successful,
            PreviewPercent(assigned, received),
            PreviewPercent(closed, received),
            PreviewPercent(successful, received),
            PreviewPercent(successful, closed));

        var closeReasons = BuildPreviewCrmCloseReasons(cards.Closed, cards.SuccessfulClosed);
        var funnels = scopedOffices
            .Select(office =>
            {
                var officeTotals = string.IsNullOrWhiteSpace(managerUserId)
                    ? new PreviewCrmAnalyticsTotals(office.Received, office.Assigned, office.Active, office.Closed, office.SuccessfulClosed)
                    : selectedManagers
                        .Where(x => x.OfficeId == office.OfficeId)
                        .Aggregate(
                            PreviewCrmAnalyticsTotals.Empty,
                            (current, manager) => current.Add(new PreviewCrmAnalyticsTotals(
                                manager.Received,
                                manager.Received,
                                manager.Active,
                                manager.Closed,
                                manager.SuccessfulClosed)));
                return BuildPreviewCrmFunnel(
                    office.OfficeId,
                    office.OfficeName,
                    Scale(officeTotals.Received),
                    Scale(officeTotals.Received));
            })
            .ToList();
        var managers = selectedManagers
            .Select(x => new CrmAnalyticsManagerDto(
                x.OfficeId,
                x.OfficeName,
                x.UserId,
                x.DisplayName,
                x.IsShiftActive,
                x.Capacity,
                x.CurrentAssignedCards,
                x.ActiveLoad,
                PreviewPercent(x.ActiveLoad, x.Capacity),
                Scale(x.Received),
                x.TasksTotal,
                x.OpenTasks,
                x.OverdueTasks))
            .OrderBy(x => x.OfficeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CrmAnalyticsDto(
            normalizedFrom,
            normalizedTo,
            officeId,
            string.IsNullOrWhiteSpace(managerUserId) ? null : managerUserId,
            cards,
            closeReasons,
            funnels,
            managerOptions,
            managers,
            DateTime.UtcNow);
    }

    private static IReadOnlyList<CrmAnalyticsCloseReasonDto> BuildPreviewCrmCloseReasons(
        int closed,
        int successful)
    {
        var remaining = Math.Max(0, closed - successful);
        var unsuccessfulReasons = CrmCloseReasons.All
            .Where(reason => !string.Equals(reason, CrmCloseReasons.Success, StringComparison.Ordinal))
            .ToArray();
        var perReason = remaining / unsuccessfulReasons.Length;
        var remainder = remaining % unsuccessfulReasons.Length;

        return CrmCloseReasons.All
            .Select((reason, index) =>
            {
                var count = string.Equals(reason, CrmCloseReasons.Success, StringComparison.Ordinal)
                    ? successful
                    : perReason + (index < remainder ? 1 : 0);
                return new CrmAnalyticsCloseReasonDto(reason, count, PreviewPercent(count, closed));
            })
            .ToArray();
    }

    private static CrmAnalyticsOfficeFunnelDto BuildPreviewCrmFunnel(
        Guid officeId,
        string officeName,
        int received,
        int currentTotal)
    {
        var stageNames = CrmStages.Default;
        var reachRatios = new[] { 1d, .82d, .7d, .6d, .51d, .43d, .36d, .3d, .25d, .2d, .16d };
        var currentWeights = Enumerable.Range(0, stageNames.Count)
            .Select(index => Math.Pow(.82d, index))
            .ToArray();
        var weightTotal = currentWeights.Sum();
        for (var i = 0; i < currentWeights.Length; i++)
        {
            currentWeights[i] /= weightTotal;
        }
        var currentCounts = AllocatePreviewCounts(currentTotal, currentWeights);
        var reachedCounts = new int[stageNames.Count];
        for (var i = 0; i < stageNames.Count; i++)
        {
            var reached = i == 0
                ? received
                : (int)Math.Round(received * reachRatios[Math.Min(i, reachRatios.Length - 1)], MidpointRounding.AwayFromZero);
            reachedCounts[i] = Math.Min(i == 0 ? received : reachedCounts[i - 1], Math.Max(0, reached));
        }

        var stages = stageNames
            .Select((stage, index) => new CrmAnalyticsFunnelStageDto(
                stage,
                index,
                currentCounts[index],
                reachedCounts[index],
                index == 0
                    ? PreviewPercent(reachedCounts[index], received)
                    : PreviewPercent(reachedCounts[index], reachedCounts[index - 1]),
                PreviewPercent(reachedCounts[index], received)))
            .ToList();
        return new CrmAnalyticsOfficeFunnelDto(officeId, officeName, received, stages);
    }

    private static int[] AllocatePreviewCounts(int total, IReadOnlyList<double> weights)
    {
        var counts = weights.Select(weight => (int)Math.Floor(total * weight)).ToArray();
        var remainder = Math.Max(0, total - counts.Sum());
        for (var i = 0; i < remainder; i++)
        {
            counts[i % counts.Length]++;
        }

        return counts;
    }

    private static double PreviewPercent(int numerator, int denominator) =>
        denominator <= 0 ? 0d : Math.Round(numerator * 100d / denominator, 2);

    public static CrmCandidateDetailDto? GetCrmCard(Guid cardId)
    {
        lock (CrmSync)
        {
            var candidate = PreviewCrmCandidates.FirstOrDefault(item => item.Id == cardId);
            if (candidate is null) return null;
            var notes = PreviewCrmNotes.TryGetValue(cardId, out var n)
                ? n.OrderByDescending(note => note.IsPinned).ThenByDescending(note => note.CreatedAtUtc).ToList()
                : [];
            var tasks = PreviewCrmTasks.Where(task => task.CardId == cardId).OrderBy(task => task.Status).ThenBy(task => task.DueAtUtc).ToList();
            var taskIds = tasks.Select(task => task.Id).ToHashSet();
            var taskComments = PreviewCrmTaskComments
                .Where(pair => taskIds.Contains(pair.Key))
                .SelectMany(pair => pair.Value)
                .OrderBy(comment => comment.CreatedAtUtc)
                .ToList();
            var history = PreviewCrmHistory.OrderByDescending(item => item.CreatedAtUtc).ToList();
            var activity = notes.Select(x => new CrmActivityItemDto(
                    "note",
                    x.IsPinned
                        ? "Закреплённый комментарий"
                        : "Комментарий",
                    x.Text,
                    x.AuthorName,
                    x.CreatedAtUtc,
                    NoteId: x.Id,
                    IsPinned: x.IsPinned,
                    CanEdit: x.CanEdit,
                    CanDelete: x.CanDelete,
                    CanPin: x.CanPin,
                    UpdatedAtUtc: x.UpdatedAtUtc))
                .Concat(tasks.Select(t =>
                {
                    var completionComment = t.Status == CrmTaskStatuses.Completed && t.CompletedAtUtc is DateTime completedAt
                        ? taskComments
                            .Where(comment => comment.TaskId == t.Id && Math.Abs((comment.CreatedAtUtc - completedAt).TotalSeconds) <= 5)
                            .OrderBy(comment => Math.Abs((comment.CreatedAtUtc - completedAt).TotalSeconds))
                            .FirstOrDefault()
                        : null;
                    return new CrmActivityItemDto(
                        t.Status == CrmTaskStatuses.Completed ? "task-done" : "task",
                        t.Title,
                        t.Description,
                        completionComment?.AuthorName ?? t.CreatorName,
                        t.CompletedAtUtc ?? t.UpdatedAtUtc ?? t.CreatedAtUtc,
                        t.Id,
                        CompletionReason: completionComment?.Text);
                }))
                .Concat(history
                    .Where(h => h.Action is not "Note"
                        and not "NoteUpdated"
                        and not "NotePinned"
                        and not "NoteUnpinned"
                        and not "TaskCreated"
                        and not "TaskUpdated"
                        and not "TaskCompleted")
                    .Select(h =>
                    {
                        var activityDetails = CrmActivityDetails.Split(h.Details);
                        return new CrmActivityItemDto(
                            "history",
                            h.Action,
                            activityDetails.Details,
                            h.ActorName,
                            h.CreatedAtUtc,
                            ActionComment: activityDetails.Comment);
                    }))
                .OrderByDescending(x => x.IsPinned)
                .ThenByDescending(x => x.AtUtc)
                .ToList();
            return new CrmCandidateDetailDto(
                ToPreviewCrmCard(candidate),
                notes,
                tasks,
                history,
                activity,
                BuildPreviewCrmManagers(),
                _previewCrmStages.ToList(),
                true,
                BuildPreviewChat(candidate.Id),
                BuildPreviewPhoneHistory(candidate),
                [new CrmContactPhoneDto(Guid.Empty, candidate.PhoneRaw, candidate.PhoneRaw, true, null, DateTime.UtcNow)],
                ChatUnreadCount: 0,
                TaskComments: taskComments,
                ClientTime: CrmClientTimeResolver.Resolve(candidate.City, DateTime.UtcNow));
        }
    }

    private static IReadOnlyList<CrmChatMessageDto> BuildPreviewChat(Guid cardId)
    {
        var now = DateTime.UtcNow;
        return
        [
            new CrmChatMessageDto(
                "Здравствуйте! Ещё актуально?",
                now.AddMinutes(-40).ToString("dd MMM HH:mm"),
                "incoming"),
            new CrmChatMessageDto(
                "Да, напишите номер — перезвоним.",
                now.AddMinutes(-25).ToString("dd MMM HH:mm"),
                "outgoing",
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                CrmOutboundChatStatuses.Sent,
                CrmOutboundChatStatuses.GetLabel(CrmOutboundChatStatuses.Sent)),
            new CrmChatMessageDto(
                "Когда вам удобно созвониться?",
                now.AddMinutes(-5).ToString("dd MMM HH:mm"),
                "outgoing",
                cardId,
                CrmOutboundChatStatuses.Planned,
                CrmOutboundChatStatuses.GetLabel(CrmOutboundChatStatuses.Planned),
                CanCancel: true)
        ];
    }

    private static IReadOnlyList<CrmPhoneHistoryDto> BuildPreviewPhoneHistory(PreviewCrmCandidate candidate) =>
        [new CrmPhoneHistoryDto(candidate.PhoneRaw, candidate.PhoneRaw, DateTime.UtcNow.AddDays(-3))];

    public static IReadOnlyList<CrmTaskDto> GetCrmTasks(string? managerUserId = null)
    {
        lock (CrmSync)
        {
            var tasks = PreviewCrmTasks.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(managerUserId))
            {
                var selectedManagerUserId = managerUserId.Trim();
                tasks = tasks.Where(task => string.Equals(
                    task.AssigneeUserId,
                    selectedManagerUserId,
                    StringComparison.Ordinal));
            }

            return tasks
                .OrderBy(task => task.Status)
                .ThenBy(task => task.DueAtUtc)
                .ThenByDescending(task => task.CreatedAtUtc)
                .ToList();
        }
    }

    public static CrmTaskDetailDto? GetCrmTask(Guid taskId)
    {
        lock (CrmSync)
        {
            var task = PreviewCrmTasks.FirstOrDefault(x => x.Id == taskId);
            if (task is null) return null;
            var comments = PreviewCrmTaskComments.TryGetValue(taskId, out var items)
                ? items.OrderBy(x => x.CreatedAtUtc).ToList()
                : [];
            var attachments = PreviewCrmTaskAttachments.TryGetValue(taskId, out var attachmentItems)
                ? attachmentItems.OrderByDescending(x => x.CreatedAtUtc).ToList()
                : [];
            return new CrmTaskDetailDto(
                task,
                comments,
                task.Status == CrmTaskStatuses.Open,
                attachments,
                true,
                BuildPreviewCrmManagers());
        }
    }

    public static CrmTaskNotificationsDto GetCrmTaskNotifications(bool unreadOnly, int limit)
    {
        lock (CrmSync)
        {
            if (!_previewCrmDeadlineNotificationsEnabled)
            {
                return new CrmTaskNotificationsDto(0, [], false);
            }

            var unreadCount = PreviewCrmTaskNotifications.Count(item => item.ReadAtUtc is null);
            var items = PreviewCrmTaskNotifications
                .Where(item => !unreadOnly || item.ReadAtUtc is null)
                .OrderByDescending(item => item.CreatedAtUtc)
                .Take(Math.Clamp(limit, 1, 50))
                .ToList();
            return new CrmTaskNotificationsDto(unreadCount, items, true);
        }
    }

    public static CrmTaskNotificationSummaryDto GetCrmTaskNotificationSummary()
    {
        lock (CrmSync)
        {
            return _previewCrmDeadlineNotificationsEnabled
                ? new CrmTaskNotificationSummaryDto(
                    PreviewCrmTaskNotifications.Count(item => item.ReadAtUtc is null),
                    true)
                : new CrmTaskNotificationSummaryDto(0, false);
        }
    }

    public static (bool Success, string? Error) MarkCrmTaskNotificationRead(Guid notificationId)
    {
        lock (CrmSync)
        {
            var index = PreviewCrmTaskNotifications.FindIndex(item => item.Id == notificationId);
            if (index < 0)
            {
                return (false, "Уведомление не найдено.");
            }

            var notification = PreviewCrmTaskNotifications[index];
            if (notification.ReadAtUtc is null)
            {
                PreviewCrmTaskNotifications[index] = notification with { ReadAtUtc = DateTime.UtcNow };
            }

            return (true, null);
        }
    }

    public static (bool Success, string? Error) MarkAllCrmTaskNotificationsRead()
    {
        lock (CrmSync)
        {
            var now = DateTime.UtcNow;
            for (var index = 0; index < PreviewCrmTaskNotifications.Count; index++)
            {
                var notification = PreviewCrmTaskNotifications[index];
                if (notification.ReadAtUtc is null)
                {
                    PreviewCrmTaskNotifications[index] = notification with { ReadAtUtc = now };
                }
            }

            return (true, null);
        }
    }

    public static (bool Success, string? Error) StartCrmShift()
    {
        lock (CrmSync)
        {
            _previewCrmShiftActive = true;
            return (true, null);
        }
    }

    public static (bool Success, string? Error) StopCrmShift()
    {
        lock (CrmSync)
        {
            _previewCrmShiftActive = false;
            return (true, null);
        }
    }

    public static (bool Success, string? Error) MoveCrmCard(Guid cardId, string stage, string? comment = null)
    {
        lock (CrmSync)
        {
            var candidate = PreviewCrmCandidates.FirstOrDefault(item => item.Id == cardId);
            if (candidate is null || !CrmStages.Contains(_previewCrmStages, stage)) return (false, "Карточка или этап не найдены.");
            if (string.IsNullOrWhiteSpace(comment)) return (false, "Нужен комментарий при смене этапа.");
            var previousStage = candidate.Stage;
            candidate.Stage = stage;
            candidate.StageChangedAtUtc = DateTime.UtcNow;
            AddPreviewCrmHistory(
                "StageChanged",
                CrmActivityDetails.WithComment($"{previousStage} → {stage}", comment));

            return (true, null);
        }
    }

    public static (bool Success, string? Error) SetCrmCardActiveLoad(Guid cardId, bool active)
    {
        lock (CrmSync)
        {
            var candidate = PreviewCrmCandidates.FirstOrDefault(item => item.Id == cardId);
            if (candidate is null) return (false, "Карточка не найдена.");
            candidate.IsInActiveLoad = active;
            AddPreviewCrmHistory(active ? "ReturnedToLoad" : "RemovedFromLoad", candidate.FullName);
            return (true, null);
        }
    }

    public static (bool Success, string? Error) AssignCrmCard(Guid cardId, string managerUserId)
    {
        lock (CrmSync)
        {
            var candidate = PreviewCrmCandidates.FirstOrDefault(item => item.Id == cardId);
            var manager = BuildPreviewCrmManagers().FirstOrDefault(m => m.UserId == managerUserId);
            if (candidate is null || manager is null) return (false, "Карточка или менеджер не найдены.");
            candidate.ManagerUserId = managerUserId;
            candidate.IsClosed = false;
            candidate.CloseReason = null;
            AddPreviewCrmHistory("Assigned", manager.DisplayName);
            return (true, null);
        }
    }

    public static (bool Success, string? Error) UpdateCrmCard(Guid cardId, CrmCardUpdateRequest request)
    {
        lock (CrmSync)
        {
            var candidate = PreviewCrmCandidates.FirstOrDefault(item => item.Id == cardId);
            if (candidate is null) return (false, "Карточка не найдена.");
            if (string.IsNullOrWhiteSpace(request.FullName)) return (false, "Укажите ФИО кандидата.");
            if (string.IsNullOrWhiteSpace(request.PhoneRaw)) return (false, "Укажите корректный телефон.");
            // Preview model fields are mostly immutable; accept edit for UI smoke only.
            AddPreviewCrmHistory("CardUpdated", "поля карточки");
            return (true, null);
        }
    }

    public static (bool Success, string? Error) CloseCrmCard(Guid cardId, string reason, string? comment)
    {
        lock (CrmSync)
        {
            var candidate = PreviewCrmCandidates.FirstOrDefault(item => item.Id == cardId);
            if (candidate is null || !CrmCloseReasons.IsValid(reason)) return (false, "Не удалось закрыть карточку.");
            if (string.IsNullOrWhiteSpace(comment)) return (false, "При закрытии сделки обязателен комментарий с причиной и деталями.");
            candidate.IsClosed = true;
            candidate.CloseReason = reason;
            candidate.IsInActiveLoad = false;
            AddPreviewCrmHistory("Closed", CrmActivityDetails.WithComment(reason, comment));
            return (true, null);
        }
    }

    public static (bool Success, string? Error) ReopenCrmCard(Guid cardId)
    {
        lock (CrmSync)
        {
            var candidate = PreviewCrmCandidates.FirstOrDefault(item => item.Id == cardId);
            if (candidate is null) return (false, "Карточка не найдена.");
            candidate.IsClosed = false;
            candidate.CloseReason = null;
            candidate.IsInActiveLoad = true;
            AddPreviewCrmHistory("Reopened", candidate.FullName);
            return (true, null);
        }
    }

    public static (bool Success, string? Error) AddCrmNote(Guid cardId, string text)
    {
        lock (CrmSync)
        {
            if (PreviewCrmCandidates.All(item => item.Id != cardId) || string.IsNullOrWhiteSpace(text)) return (false, "Введите текст заметки.");
            if (!PreviewCrmNotes.TryGetValue(cardId, out var notes))
            {
                notes = [];
                PreviewCrmNotes[cardId] = notes;
            }
            notes.Add(new CrmNoteDto(Guid.NewGuid(), "preview-admin", "Администратор", text.Trim(), DateTime.UtcNow, CanEdit: true, CanDelete: true, CanPin: true));
            AddPreviewCrmHistory("Note", text.Trim());
            return (true, null);
        }
    }

    public static (bool Success, string? Error) UpdateCrmNote(Guid cardId, Guid noteId, string text)
    {
        lock (CrmSync)
        {
            if (string.IsNullOrWhiteSpace(text)
                || !PreviewCrmNotes.TryGetValue(cardId, out var notes))
            {
                return (false, "Введите текст комментария.");
            }

            var index = notes.FindIndex(note => note.Id == noteId);
            if (index < 0) return (false, "Комментарий не найден.");
            notes[index] = notes[index] with { Text = text.Trim(), UpdatedAtUtc = DateTime.UtcNow };
            AddPreviewCrmHistory("NoteUpdated", text.Trim());
            return (true, null);
        }
    }

    public static (bool Success, string? Error) DeleteCrmNote(Guid cardId, Guid noteId)
    {
        lock (CrmSync)
        {
            if (!PreviewCrmNotes.TryGetValue(cardId, out var notes))
            {
                return (false, "Комментарий не найден.");
            }

            var index = notes.FindIndex(note => note.Id == noteId);
            if (index < 0) return (false, "Комментарий не найден.");
            notes.RemoveAt(index);
            AddPreviewCrmHistory("NoteDeleted", noteId.ToString("D"));
            return (true, null);
        }
    }

    public static (bool Success, string? Error) SetCrmNotePinned(Guid cardId, Guid noteId, bool isPinned)
    {
        lock (CrmSync)
        {
            if (!PreviewCrmNotes.TryGetValue(cardId, out var notes))
            {
                return (false, "Комментарий не найден.");
            }

            var index = notes.FindIndex(note => note.Id == noteId);
            if (index < 0) return (false, "Комментарий не найден.");
            notes[index] = notes[index] with { IsPinned = isPinned, CanEdit = true, CanDelete = true, CanPin = true };
            AddPreviewCrmHistory(isPinned ? "NotePinned" : "NoteUnpinned", notes[index].Text);
            return (true, null);
        }
    }

    public static (CrmTaskDto? Task, string? Error) CreateCrmTask(CrmTaskCreateRequest request)
    {
        lock (CrmSync)
        {
            if (string.IsNullOrWhiteSpace(request.Title)
                || !CrmTaskImportances.IsValid(request.Importance)
                || !CrmTaskTypes.IsValid(request.TaskType))
            {
                return (null, "Проверьте название, тип и важность задачи.");
            }
            var assignee = BuildPreviewCrmManagers().FirstOrDefault(manager => manager.UserId == request.AssigneeUserId);
            if (assignee is null) return (null, "Исполнитель не найден.");
            var candidateName = request.CardId is Guid id
                ? PreviewCrmCandidates.FirstOrDefault(c => c.Id == id)?.FullName
                : null;
            var due = request.DueAtUtc;
            var task = new CrmTaskDto(Guid.NewGuid(), request.CardId, candidateName, request.Title.Trim(), request.Description?.Trim(), assignee.UserId, assignee.DisplayName, "preview-admin", "Администратор", due, CrmTaskStatuses.Open, DateTime.UtcNow, null, due is DateTime d && d < DateTime.UtcNow, request.Importance, request.TaskType);
            PreviewCrmTasks.Add(task);
            AddPreviewCrmHistory("TaskCreated", task.Title);
            return (task, null);
        }
    }

    public static (CrmTaskDto? Task, string? Error) CreateCrmFollowUp(Guid cardId, int minutes, string? title)
    {
        lock (CrmSync)
        {
            var label = string.IsNullOrWhiteSpace(title)
                ? minutes <= 60 ? "Перезвонить через час" : $"Перезвонить через {minutes / 60} ч"
                : title.Trim();
            return CreateCrmTask(new CrmTaskCreateRequest(
                cardId,
                label,
                null,
                PreviewManagerElena,
                DateTime.UtcNow.AddMinutes(minutes),
                CrmTaskImportances.Medium,
                CrmTaskTypes.CallBack));
        }
    }

    public static (bool Success, string? Error) CompleteCrmTask(Guid taskId, string? comment = null)
    {
        lock (CrmSync)
        {
            if (string.IsNullOrWhiteSpace(comment)) return (false, "При выполнении задачи обязателен комментарий: что сделано.");
            var index = PreviewCrmTasks.FindIndex(task => task.Id == taskId);
            if (index < 0) return (false, "Задача не найдена.");
            var task = PreviewCrmTasks[index];
            if (task.Status != CrmTaskStatuses.Open) return (false, "Выполнить можно только задачу в работе.");
            var completedAt = DateTime.UtcNow;
            PreviewCrmTasks[index] = task with { Status = CrmTaskStatuses.Completed, CompletedAtUtc = completedAt, IsOverdue = false };
            if (!PreviewCrmTaskComments.TryGetValue(taskId, out var comments))
            {
                comments = [];
                PreviewCrmTaskComments[taskId] = comments;
            }

            comments.Add(new CrmTaskCommentDto(
                Guid.NewGuid(),
                taskId,
                "preview-admin",
                "Администратор",
                comment.Trim(),
                completedAt,
                CanEdit: true,
                CanDelete: true));
            AddPreviewCrmHistory("TaskCompleted", task.Title);
            return (true, null);
        }
    }

    public static (bool Success, string? Error) UpdateCrmTask(Guid taskId, CrmTaskUpdateRequest update)
    {
        lock (CrmSync)
        {
            var index = PreviewCrmTasks.FindIndex(task => task.Id == taskId);
            if (index < 0) return (false, "Задача не найдена.");
            var task = PreviewCrmTasks[index];
            var taskType = update.TaskType ?? task.TaskType;
            if (string.IsNullOrWhiteSpace(update.Title)
                || !CrmTaskImportances.IsValid(update.Importance)
                || !CrmTaskTypes.IsValid(taskType))
            {
                return (false, "Проверьте название, тип и важность задачи.");
            }

            if (task.Status != CrmTaskStatuses.Open) return (false, "Можно изменить только задачу в работе.");
            var assignee = BuildPreviewCrmManagers().FirstOrDefault(manager => manager.UserId == update.AssigneeUserId);
            if (assignee is null) return (false, "Исполнитель не найден.");
            PreviewCrmTasks[index] = task with
            {
                Title = update.Title.Trim(),
                Description = string.IsNullOrWhiteSpace(update.Description) ? null : update.Description.Trim(),
                AssigneeUserId = assignee.UserId,
                AssigneeName = assignee.DisplayName,
                DueAtUtc = update.DueAtUtc,
                Importance = update.Importance,
                TaskType = taskType,
                UpdatedAtUtc = DateTime.UtcNow,
                IsOverdue = update.DueAtUtc is DateTime due && due < DateTime.UtcNow
            };
            AddPreviewCrmHistory("TaskUpdated", update.Title.Trim());
            return (true, null);
        }
    }

    public static (bool Success, string? Error) CancelCrmTask(Guid taskId)
    {
        lock (CrmSync)
        {
            var index = PreviewCrmTasks.FindIndex(task => task.Id == taskId);
            if (index < 0) return (false, "Задача не найдена.");
            var task = PreviewCrmTasks[index];
            if (task.Status != CrmTaskStatuses.Open) return (false, "Отменить можно только задачу в работе.");
            PreviewCrmTasks[index] = task with
            {
                Status = CrmTaskStatuses.Cancelled,
                CompletedAtUtc = DateTime.UtcNow,
                IsOverdue = false
            };
            AddPreviewCrmHistory("TaskCancelled", task.Title);
            return (true, null);
        }
    }

    public static (bool Success, string? Error) ReopenCrmTask(Guid taskId)
    {
        lock (CrmSync)
        {
            var index = PreviewCrmTasks.FindIndex(task => task.Id == taskId);
            if (index < 0) return (false, "Задача не найдена.");
            var task = PreviewCrmTasks[index];
            PreviewCrmTasks[index] = task with
            {
                Status = CrmTaskStatuses.Open,
                CompletedAtUtc = null,
                IsOverdue = task.DueAtUtc is DateTime due && due < DateTime.UtcNow
            };
            AddPreviewCrmHistory("TaskReopened", task.Title);
            return (true, null);
        }
    }

    public static (bool Success, string? Error) DeleteCrmTask(Guid taskId)
    {
        lock (CrmSync)
        {
            var index = PreviewCrmTasks.FindIndex(task => task.Id == taskId);
            if (index < 0) return (false, "Задача не найдена.");
            var task = PreviewCrmTasks[index];
            PreviewCrmTasks.RemoveAt(index);
            PreviewCrmTaskComments.Remove(taskId);
            if (PreviewCrmTaskAttachments.Remove(taskId, out var attachments))
            {
                foreach (var attachment in attachments)
                {
                    PreviewCrmTaskAttachmentContent.Remove(attachment.Id);
                }
            }
            AddPreviewCrmHistory("TaskDeleted", task.Title);
            return (true, null);
        }
    }

    public static (bool Success, string? Error) AddCrmTaskComment(Guid taskId, string text)
    {
        lock (CrmSync)
        {
            if (PreviewCrmTasks.All(task => task.Id != taskId) || string.IsNullOrWhiteSpace(text))
            {
                return (false, "Введите текст комментария.");
            }

            if (!PreviewCrmTaskComments.TryGetValue(taskId, out var comments))
            {
                comments = [];
                PreviewCrmTaskComments[taskId] = comments;
            }

            comments.Add(new CrmTaskCommentDto(
                Guid.NewGuid(),
                taskId,
                "preview-admin",
                "Администратор",
                text.Trim(),
                DateTime.UtcNow,
                CanEdit: true,
                CanDelete: true));
            return (true, null);
        }
    }

    public static (bool Success, string? Error) UpdateCrmTaskComment(Guid taskId, Guid commentId, string text)
    {
        lock (CrmSync)
        {
            if (string.IsNullOrWhiteSpace(text)
                || !PreviewCrmTaskComments.TryGetValue(taskId, out var comments))
            {
                return (false, "Введите текст комментария.");
            }

            var index = comments.FindIndex(comment => comment.Id == commentId);
            if (index < 0) return (false, "Комментарий не найден.");
            comments[index] = comments[index] with { Text = text.Trim(), UpdatedAtUtc = DateTime.UtcNow };
            return (true, null);
        }
    }

    public static (bool Success, string? Error) DeleteCrmTaskComment(Guid taskId, Guid commentId)
    {
        lock (CrmSync)
        {
            if (!PreviewCrmTaskComments.TryGetValue(taskId, out var comments))
            {
                return (false, "Комментарий не найден.");
            }

            var index = comments.FindIndex(comment => comment.Id == commentId);
            if (index < 0) return (false, "Комментарий не найден.");
            comments.RemoveAt(index);
            return (true, null);
        }
    }

    public static (CrmTaskAttachmentDto? Attachment, string? Error) AddCrmTaskAttachment(
        Guid taskId,
        Stream content,
        long contentLength,
        string fileName,
        string? contentType)
    {
        lock (CrmSync)
        {
            if (PreviewCrmTasks.All(task => task.Id != taskId))
            {
                return (null, "Задача не найдена.");
            }

            if (contentLength <= 0 || contentLength > CrmTaskAttachmentLimits.MaxFileSizeBytes)
            {
                return (null, "Размер файла вне допустимого диапазона.");
            }

            var safeFileName = Path.GetFileName(fileName?.Trim() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                return (null, "Выберите файл.");
            }

            using var copy = new MemoryStream();
            content.CopyTo(copy);
            var attachment = new CrmTaskAttachmentDto(
                Guid.NewGuid(),
                safeFileName,
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                contentLength,
                "Администратор",
                DateTime.UtcNow);
            if (!PreviewCrmTaskAttachments.TryGetValue(taskId, out var attachments))
            {
                attachments = [];
                PreviewCrmTaskAttachments[taskId] = attachments;
            }

            attachments.Add(attachment);
            PreviewCrmTaskAttachmentContent[attachment.Id] = copy.ToArray();
            return (attachment, null);
        }
    }

    public static (Stream? Stream, string? FileName, string? ContentType) OpenCrmTaskAttachment(Guid taskId, Guid attachmentId)
    {
        lock (CrmSync)
        {
            var attachment = PreviewCrmTaskAttachments.TryGetValue(taskId, out var attachments)
                ? attachments.FirstOrDefault(item => item.Id == attachmentId)
                : null;
            if (attachment is null || !PreviewCrmTaskAttachmentContent.TryGetValue(attachmentId, out var content))
            {
                return (null, null, null);
            }

            return (new MemoryStream(content, writable: false), attachment.FileName, attachment.ContentType);
        }
    }

    public static (bool Success, string? Error) SetCrmOfficeSettings(
        bool enabled,
        bool requireStageComment,
        bool? deadlineNotificationsEnabled = null)
    {
        lock (CrmSync)
        {
            _previewCrmEnabled = enabled;
            if (deadlineNotificationsEnabled is bool notificationsEnabled)
            {
                _previewCrmDeadlineNotificationsEnabled = notificationsEnabled;
            }
            return (true, null);
        }
    }

    public static (bool Success, string? Error) SetCrmOfficeFunnel(IReadOnlyList<string> stages)
    {
        lock (CrmSync)
        {
            var normalized = CrmStages.Normalize(stages);
            if (normalized is null)
            {
                return (false, $"Укажите от {CrmStages.MinCount} до {CrmStages.MaxCount} уникальных этапов.");
            }

            var nextSet = new HashSet<string>(normalized, StringComparer.Ordinal);
            var fallback = normalized[0];
            foreach (var card in PreviewCrmCandidates.Where(c => !c.IsClosed && !nextSet.Contains(c.Stage)))
            {
                card.Stage = fallback;
                card.StageChangedAtUtc = DateTime.UtcNow;
            }

            _previewCrmStages = normalized.ToList();
            return (true, null);
        }
    }

    public static (bool Success, string? Error) SetCrmManagerCapacity(string managerUserId, int capacity)
    {
        lock (CrmSync)
        {
            if (capacity is < 1 or > 300) return (false, "Ёмкость 1–300.");
            if (managerUserId is not (PreviewManagerElena or PreviewManagerIgor)) return (false, "Менеджер не найден.");
            return (true, null);
        }
    }

    private static IReadOnlyList<CrmManagerDto> BuildPreviewCrmManagers()
    {
        var now = DateTime.UtcNow;
        return
        [
            new(
                PreviewManagerElena,
                "Елена Воронцова",
                _previewCrmShiftActive,
                10,
                PreviewCrmCandidates.Count(candidate => candidate.ManagerUserId == PreviewManagerElena && candidate.IsInActiveLoad && !candidate.IsClosed),
                _previewCrmShiftActive ? now.AddHours(-3).AddMinutes(-20) : null,
                _previewCrmShiftActive ? null : now.AddHours(-5)),
            new(
                PreviewManagerIgor,
                "Игорь Белов",
                true,
                10,
                PreviewCrmCandidates.Count(candidate => candidate.ManagerUserId == PreviewManagerIgor && candidate.IsInActiveLoad && !candidate.IsClosed),
                now.AddHours(-1).AddMinutes(-5),
                now.AddDays(-1).AddHours(-2))
        ];
    }

    private static CrmCandidateCardDto ToPreviewCrmCard(PreviewCrmCandidate candidate)
    {
        var openTasks = PreviewCrmTasks.Count(t => t.CardId == candidate.Id && t.Status == CrmTaskStatuses.Open);
        var overdue = PreviewCrmTasks.Any(t => t.CardId == candidate.Id && t.IsOverdue && t.Status == CrmTaskStatuses.Open);
        var hours = Math.Round(Math.Max(0, (DateTime.UtcNow - candidate.StageChangedAtUtc).TotalHours), 1);
        return new(
            candidate.Id,
            Guid.Parse($"94000000-0000-0000-0000-{candidate.Id.ToString("N")[^12..]}"),
            candidate.FullName,
            candidate.Age,
            candidate.PhoneRaw,
            candidate.City,
            candidate.Vacancy,
            $"https://www.avito.ru/profile/messenger/{candidate.Id:N}",
            candidate.Stage,
            candidate.ManagerUserId,
            candidate.ManagerUserId == PreviewManagerElena ? "Елена Воронцова" : candidate.ManagerUserId == PreviewManagerIgor ? "Игорь Белов" : null,
            candidate.IsInActiveLoad,
            candidate.CreatedAtUtc,
            candidate.StageChangedAtUtc,
            candidate.LastContactAtUtc,
            candidate.NextActionAtUtc,
            candidate.IsClosed,
            candidate.CloseReason,
            openTasks,
            overdue,
            hours,
            $"https://www.avito.ru/item/{candidate.Id:N}",
            $"https://www.avito.ru/item/{candidate.Id:N}",
            "Avito · Северный парк",
            candidate.Id.ToString("N")[..8],
            Citizenship: candidate.Citizenship);
    }

    private static void AddPreviewCrmHistory(string action, string details) =>
        PreviewCrmHistory.Add(new CrmHistoryDto(Guid.NewGuid(), action, details, "preview-admin", "Администратор", DateTime.UtcNow));

    private sealed class PreviewCrmCandidate(Guid id, string fullName, int? age, string phoneRaw, string city, string vacancy, string stage, string? managerUserId, bool isInActiveLoad, int minutesAgo)
    {
        public Guid Id { get; } = id;
        public string FullName { get; } = fullName;
        public int? Age { get; } = age;
        public string PhoneRaw { get; } = phoneRaw;
        public string City { get; } = city;
        public string Vacancy { get; } = vacancy;
        public string Citizenship { get; } = "Россия";
        public string Stage { get; set; } = stage;
        public string? ManagerUserId { get; set; } = managerUserId;
        public bool IsInActiveLoad { get; set; } = isInActiveLoad;
        public DateTime CreatedAtUtc { get; } = Now.AddMinutes(-minutesAgo);
        public DateTime StageChangedAtUtc { get; set; } = Now.AddMinutes(-minutesAgo);
        public DateTime? LastContactAtUtc { get; set; } = Now.AddMinutes(-minutesAgo / 2);
        public DateTime? NextActionAtUtc { get; set; } = Now.AddHours(2);
        public bool IsClosed { get; set; }
        public string? CloseReason { get; set; }
    }

    private sealed record PreviewCrmAnalyticsOffice(
        Guid OfficeId,
        string OfficeName,
        int Received,
        int Assigned,
        int Active,
        int Closed,
        int SuccessfulClosed);

    private sealed record PreviewCrmAnalyticsManager(
        Guid OfficeId,
        string OfficeName,
        string UserId,
        string DisplayName,
        bool IsShiftActive,
        int Capacity,
        int CurrentAssignedCards,
        int ActiveLoad,
        int Received,
        int Active,
        int Closed,
        int SuccessfulClosed,
        int TasksTotal,
        int OpenTasks,
        int OverdueTasks);

    private sealed record PreviewCrmAnalyticsTotals(
        int Received,
        int Assigned,
        int Active,
        int Closed,
        int SuccessfulClosed)
    {
        public static PreviewCrmAnalyticsTotals Empty { get; } = new(0, 0, 0, 0, 0);

        public PreviewCrmAnalyticsTotals Add(PreviewCrmAnalyticsTotals other) =>
            new(
                Received + other.Received,
                Assigned + other.Assigned,
                Active + other.Active,
                Closed + other.Closed,
                SuccessfulClosed + other.SuccessfulClosed);
    }

    public static GlobalDashboardSummary Summary => new(
        TotalWorkers: 3,
        OnlineWorkers: 3,
        TotalToday: 1234,
        SentToCrm: 1100,
        InProgress: 42,
        Duplicates: 256,
        Errors: 18,
        ActionRequired: 0,
        ConnectedAccounts: 30,
        RequiresAuthorization: 0,
        AccountsNeedAttentionCount: 0,
        AccountStatusCounts: new DashboardAccountStatusCounts(24, 4, 1, 1),
        ActiveAdsCount: 120,
        BlockedAdsCount: 0,
        TotalBalance: 48_500m,
        HourlyActivity: BuildHourly(),
        WeeklyByDayActivity: BuildWeekly(),
        AggregatedAtUtc: Now);

    public static IReadOnlyList<WorkerListItem> Workers => BuildWorkerListItems();

    public static IReadOnlyList<WorkerListItem> GetWorkers(Guid? officeId) =>
        FilterByOffice(BuildWorkerListItems(), officeId, x => x.OfficeId);

    public static GlobalDashboardSummary GetSummary(Guid? officeId)
    {
        if (officeId is null)
        {
            return Summary;
        }

        var workers = GetWorkers(officeId);
        var ratio = workers.Count / (double)Math.Max(1, BuildWorkerListItems().Count);
        return Summary with
        {
            TotalWorkers = workers.Count,
            OnlineWorkers = workers.Count(w => w.IsOnline),
            TotalToday = (int)Math.Round(Summary.TotalToday * ratio),
            SentToCrm = (int)Math.Round(Summary.SentToCrm * ratio),
            InProgress = (int)Math.Round(Summary.InProgress * ratio),
            Duplicates = (int)Math.Round(Summary.Duplicates * ratio),
            Errors = (int)Math.Round(Summary.Errors * ratio),
            ConnectedAccounts = (int)Math.Round(Summary.ConnectedAccounts * ratio),
            TotalBalance = Math.Round(Summary.TotalBalance * (decimal)ratio, 2)
        };
    }

    public static IReadOnlyList<WorkerEventListItem> GetEvents(Guid? officeId)
    {
        var workerIds = GetWorkers(officeId).Select(x => x.Id).ToHashSet();
        return Events.Where(x => workerIds.Contains(x.WorkerId)).ToList();
    }

    public static ResponsesPageDto GetResponsesPage(string query, Guid? officeId) =>
        new([], 0, 1, 10);

    public static ResponsesSummaryDto GetResponsesSummary(Guid? officeId) =>
        new(0, 0, 0, 0, 0, null);

    public static OfficeStatisticsDto GetStatistics(
        Guid? officeId,
        DateTime from,
        DateTime to,
        IReadOnlyList<Guid>? workerIds = null,
        IReadOnlyList<Guid>? accountIds = null)
    {
        var summary = GetSummary(officeId);
        var workers = GetWorkers(officeId);
        if (workerIds is { Count: > 0 })
        {
            var workerFilter = workerIds.ToHashSet();
            workers = workers.Where(w => workerFilter.Contains(w.Id)).ToList();
        }
        var days = Math.Max(1, (to.Date - from.Date).Days + 1);
        var dailyTrend = BuildStatisticsDailyTrend(from.Date, days, summary);

        var balanceAccounts = new List<AccountBalanceStatDto>
        {
            new(AccountAlphaId, "Альфа HR", WorkerMoscowId, "msk-worker-01", "Москва", 18500m, 3200m,
                [new SubProfileBalanceDto("Основной", 12000m, 2000m, "~ на 12 дней"), new SubProfileBalanceDto("Доп.", 6500m, 1200m, "~ на 5 дней")],
                false),
            new(AccountBetaId, "Бета Кадры", WorkerSpbId, "spb-worker-02", "Санкт-Петербург", 4200m, 800m,
                [new SubProfileBalanceDto("Основной", 4200m, 800m, "~ на 3 дня")],
                true),
            new(AccountGammaId, "Гамма Рекрут", WorkerKazanId, "kzn-worker-03", "Казань", 25800m, 5100m,
                [new SubProfileBalanceDto("Основной", 25800m, 5100m, "~ на 18 дней")],
                false)
        };

        if (officeId is Guid officeFilter)
        {
            balanceAccounts = balanceAccounts
                .Where(a => workers.Any(w => w.Id == a.WorkerId))
                .ToList();
        }

        if (accountIds is { Count: > 0 })
        {
            var accountFilter = accountIds.ToHashSet();
            balanceAccounts = balanceAccounts.Where(a => accountFilter.Contains(a.AccountId)).ToList();
        }

        return new OfficeStatisticsDto(
            new BalanceStatisticsSection(
                balanceAccounts.Sum(a => a.Advance),
                balanceAccounts.Sum(a => a.Wallet),
                balanceAccounts.Count(a => a.IsLowBalance),
                balanceAccounts),
            new AccountInfrastructureSection(
                summary.ConnectedAccounts,
                summary.AccountStatusCounts,
                summary.ActiveAdsCount,
                summary.BlockedAdsCount),
            new WorkerInfrastructureSection(
                workers.Count,
                workers.Count(w => w.IsOnline),
                workers.Select(w => new WorkerStatisticsRowDto(
                    w.Id,
                    w.DisplayName,
                    w.OfficeName,
                    w.IsOnline,
                    w.TotalToday,
                    Math.Max(0, w.TotalToday - w.DuplicatesToday - w.Errors),
                    w.DuplicatesToday,
                    w.Errors,
                    w.ActiveAccountCount,
                    w.AccountCount)).ToList()),
            new ResponsesPeriodSection(
                dailyTrend.Sum(d => d.Total),
                dailyTrend.Sum(d => d.Total - d.Duplicates),
                dailyTrend.Sum(d => d.Duplicates),
                dailyTrend.Sum(d => d.Sent),
                dailyTrend.Sum(d => d.InProgress),
                0,
                dailyTrend.Sum(d => d.Errors),
                84,
                17.5),
            dailyTrend,
            [
                new BitrixDeliveryStatDto(Guid.Parse("11111111-1111-1111-1111-111111111101"), "HR Москва", 186),
                new BitrixDeliveryStatDto(Guid.Parse("11111111-1111-1111-1111-111111111102"), "Кадры СПб", 124)
            ],
            [
                new CrmDeliveryStatDto(Guid.Parse("11111111-1111-1111-1111-111111111201"), "Офис Москва", 94),
                new CrmDeliveryStatDto(Guid.Parse("11111111-1111-1111-1111-111111111202"), "Офис Екатеринбург", 58)
            ],
            new HrInsightsDto(
                [new HrMetricDto("Москва", 420, 310, "73.8%", "34.1%"), new HrMetricDto("Санкт-Петербург", 280, 190, "67.9%", "22.7%")],
                [new HrMetricDto("Курьер", 360, 250, "69.4%", "29.2%"), new HrMetricDto("Водитель", 210, 140, "66.7%", "17.0%")],
                [new HrMetricDto("Альфа HR", 190, 140, "73.7%", "15.4%"), new HrMetricDto("Бета Кадры", 150, 95, "63.3%", "12.2%")],
                [new AgeBucketDto("18-24", 180, 120, "66.7%"), new AgeBucketDto("25-34", 260, 180, "69.2%"), new AgeBucketDto("35-44", 140, 90, "64.3%")],
                "28.4 лет",
                "76.5%"),
            BuildMonitoringCyclePreview(from, to),
            Now);
    }

    private static MonitoringCycleReportDto BuildMonitoringCyclePreview(DateTime from, DateTime to)
    {
        var leadSummaries = new List<MonitoringCycleLeadSummaryDto>
        {
            new("Авито 1", 12, ["4/10 (Контракт РФ 4) = 1", "7/10 (контракт РФ 7) = 6"]),
            new("Авито 17", 34, ["10/10 (Контракт10) = 26"]),
            new("Авито 30", 32, ["6/10 (Работа вахтой2) = 13", "9/10 (Кадровый отдел7) = 11"])
        };

        var previewDayUtc = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var secondDayUtc = DateTime.SpecifyKind(from.Date.AddDays(1), DateTimeKind.Utc);
        var multiDay = (to.Date - from.Date).Days > 0;
        var accountReports = new List<MonitoringCycleAccountReportDto>
        {
            new(
                "Авито 1",
                previewDayUtc,
                10,
                8,
                12,
                [
                    new MonitoringCycleSubProfileRowDto(
                        7,
                        10,
                        "контракт РФ 7",
                        [
                            previewDayUtc.AddHours(2).AddMinutes(3).AddSeconds(16),
                            previewDayUtc.AddHours(7).AddMinutes(22).AddSeconds(59),
                            previewDayUtc.AddHours(15).AddMinutes(18).AddSeconds(9)
                        ],
                        ["2", "0", "1"],
                        [],
                        WasStarted: true),
                    new MonitoringCycleSubProfileRowDto(
                        10,
                        10,
                        "контракт РФ 10",
                        [
                            previewDayUtc.AddHours(2).AddMinutes(5).AddSeconds(50),
                            previewDayUtc.AddHours(10).AddMinutes(7).AddSeconds(46),
                            previewDayUtc.AddHours(20).AddMinutes(11).AddSeconds(35)
                        ],
                        ["0", "1", "0"],
                        [],
                        WasStarted: true)
                ],
                [])
        };

        if (multiDay)
        {
            accountReports.Add(new(
                "Авито 1",
                secondDayUtc,
                10,
                6,
                8,
                [
                    new MonitoringCycleSubProfileRowDto(
                        7,
                        10,
                        "контракт РФ 7",
                        [secondDayUtc.AddHours(11).AddMinutes(4)],
                        ["1"],
                        [],
                        WasStarted: true)
                ],
                []));
        }

        return new MonitoringCycleReportDto(
            true,
            leadSummaries.Sum(x => x.TotalLeads),
            1,
            10,
            ["  Авито 34: 10 не запущены — 1/10 (Кадровый Отдел10), 2/10 (Кадровый отдел9)"],
            leadSummaries,
            accountReports);
    }

    public static StatisticsViewModel BuildStatisticsIndexViewModel(
        DashboardPeriod period,
        IOfficeContext officeContext,
        StatisticsFiltersViewModel filters) =>
        StatisticsIndexBuilder.Build(
            GetStatistics(
                officeContext.EffectiveOfficeId,
                period.From,
                period.To,
                filters.WorkerIds,
                filters.AccountIds),
            period,
            officeContext,
            filters,
            GetWorkers(officeContext.EffectiveOfficeId)
                .OrderBy(w => w.DisplayName)
                .Select(w => new EventFilterOptionViewModel
                {
                    Value = w.Id.ToString(),
                    Label = w.DisplayName
                })
                .ToList(),
            [],
            FilterChipsBuilder.ForStatistics(filters, period, [], []));

    public static WorkersIndexViewModel BuildWorkersIndexViewModel(
        string? searchQuery,
        string? status,
        int page,
        int pageSize,
        string? sort = null,
        string? sortDir = null,
        Guid? officeId = null,
        bool showOfficeColumn = false)
    {
        var rows = FilterWorkerRowsByOffice(BuildWorkerRows(), officeId);
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            rows = rows
                .Where(w => SearchQueryNormalizer.MatchesTokens(searchQuery, w.DisplayName, w.MachineName))
                .ToList();
        }

        status = status?.Trim().ToLowerInvariant() switch
        {
            "online" => "online",
            "offline" => "offline",
            _ => null
        };

        if (status == "online")
        {
            rows = rows.Where(w => w.IsOnline).ToList();
        }
        else if (status == "offline")
        {
            rows = rows.Where(w => !w.IsOnline).ToList();
        }

        var tableSort = TableSort.Parse(sort, sortDir, TableSort.Workers.Default, TableSort.Workers.Columns);
        var sorted = TableSort.Workers.Apply(rows, tableSort).ToList();
        var total = sorted.Count;
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var online = sorted.Count(w => w.IsOnline);
        var offline = rows.Count - online;

        return new WorkersIndexViewModel
        {
            Header = PageHeaderBuilder.WorkersList(),
            SearchQuery = searchQuery,
            StatusFilter = status,
            KpiCards =
            [
                new() { Key = "total", Href = KpiCardLinks.WorkersCard("total"), Label = "Всего воркеров", Value = total.ToString(), CountValue = total, IconClass = "fa-solid fa-server", IconTone = "blue" },
                new() { Key = "online", Href = KpiCardLinks.WorkersCard("online"), Label = "Онлайн", Value = online.ToString(), CountValue = online, IconClass = "fa-solid fa-circle-check", IconTone = "green" },
                new() { Key = "offline", Href = KpiCardLinks.WorkersCard("offline"), Label = "Оффлайн", Value = offline.ToString(), CountValue = offline, IconClass = "fa-solid fa-circle-xmark", IconTone = "orange" },
                new() { Key = "responses", Href = KpiCardLinks.WorkersCard("responses"), Label = "Всего откликов", Value = rows.Sum(w => w.Responses).ToString(), CountValue = rows.Sum(w => w.Responses), IconClass = "fa-regular fa-comments", IconTone = "blue" },
                new() { Key = "errors", Href = KpiCardLinks.WorkersCard("errors"), Label = "Ошибок", Value = rows.Sum(w => w.Errors).ToString(), CountValue = rows.Sum(w => w.Errors), IconClass = "fa-solid fa-triangle-exclamation", IconTone = "orange" }
            ],
            Workers = paged,
            Pagination = new PaginationViewModel { Page = page, PageSize = pageSize, TotalItems = total },
            HasWorkerRelease = true,
            LatestWorkerReleaseVersion = "1.0.0.2",
            LatestWorkerDownloadUrl = "/Workers/DownloadLatest",
            CanCreateWorker = true,
            HasActiveFilters = !string.IsNullOrWhiteSpace(searchQuery) || !string.IsNullOrWhiteSpace(status),
            ActiveFilterChips = FilterChipsBuilder.ForWorkers(searchQuery, status, pageSize),
            Sort = tableSort,
            ShowOfficeColumn = showOfficeColumn
        };
    }

    private static IReadOnlyList<WorkerListItem> BuildWorkerListItems() =>
        BuildWorkerRows().Select((w, i) => new WorkerListItem(
            w.Id,
            w.DisplayName,
            $"WIN-W{(i + 1):D2}",
            "2.4.1",
            w.IsOnline ? "Running" : "Stopped",
            w.IsOnline ? null : "Нет heartbeat",
            w.IsOnline,
            w.IsOnline,
            w.LastActivityUtc?.ToUniversalTime(),
            w.TotalAccounts,
            w.Responses,
            w.Duplicates,
            w.Errors,
            w.UpdateAvailable,
            w.LatestReleaseVersion,
            i >= 6 ? PreviewOffice2Id : PreviewOfficeId,
            i >= 6 ? "Сибирь" : "Основной",
            true,
            w.ActiveAccounts,
            BuildPreviewWorkerActivity(i, w.IsOnline))).ToList();

    private static IReadOnlyList<WorkerRowViewModel> BuildWorkerRows()
    {
        DateTime?[] times =
        [
            Now.AddSeconds(-12), Now.AddSeconds(-8), Now.AddSeconds(-15),
            Now.AddMinutes(-2), Now.AddMinutes(-4), Now.AddMinutes(-6),
            Now.AddMinutes(-9), Now.AddMinutes(-11), Now.AddMinutes(-14),
            Now.AddMinutes(-22), Now.AddHours(-1), null
        ];

        var accounts = new (int Active, int Total)[]
        {
            (10, 10), (10, 10), (10, 10), (10, 10), (8, 10), (10, 10),
            (9, 10), (10, 10), (7, 10), (10, 10), (6, 10), (0, 10)
        };

        var responses = new[] { 432, 401, 401, 388, 356, 342, 318, 295, 271, 248, 192, 0 };
        var duplicates = new[] { 98, 87, 71, 64, 58, 52, 47, 41, 36, 29, 18, 0 };
        var errors = new[] { 5, 8, 5, 4, 6, 3, 2, 4, 1, 2, 3, 0 };
        var online = new[] { true, true, true, true, true, true, true, true, true, true, true, false };
        var enabled = new[] { true, true, true, true, true, true, true, true, true, true, true, false };

        return Enumerable.Range(0, 12).Select(i =>
        {
            var activityDto = BuildPreviewWorkerActivity(i, online[i]);
            var activity = WorkerActivityPresenter.Present(activityDto, online[i]);
            return new WorkerRowViewModel
            {
                Id = PreviewWorkerIds[i],
                DisplayName = $"Worker #{i + 1}",
                MachineName = $"WIN-W{(i + 1):D2}",
                IsOnline = online[i],
                IsEnabled = enabled[i],
                UpdateAvailable = i < 3,
                LatestReleaseVersion = "1.0.0.2",
                ActiveAccounts = accounts[i].Active,
                TotalAccounts = accounts[i].Total,
                Responses = responses[i],
                Duplicates = duplicates[i],
                Errors = errors[i],
                LastActivityUtc = times[i],
                CurrentActivityLabel = activity.Label,
                CurrentActivityTone = activity.Tone,
                IsActivityLive = activity.IsLive,
                OfficeName = i >= 6 ? "Сибирь" : "Основной"
            };
        }).ToList();
    }

    private static List<T> FilterByOffice<T>(IEnumerable<T> rows, Guid? officeId, Func<T, Guid> officeSelector) =>
        officeId is Guid id
            ? rows.Where(x => officeSelector(x) == id).ToList()
            : rows.ToList();

    private static IReadOnlyList<WorkerRowViewModel> FilterWorkerRowsByOffice(
        IReadOnlyList<WorkerRowViewModel> rows,
        Guid? officeId) =>
        officeId is Guid id
            ? rows.Where(x => string.Equals(
                x.OfficeName,
                id == PreviewOffice2Id ? "Сибирь" : "Основной",
                StringComparison.Ordinal)).ToList()
            : rows;

    private static WorkerActivityDto? BuildPreviewWorkerActivity(int index, bool isOnline)
    {
        if (!isOnline)
        {
            return null;
        }

        return index switch
        {
            0 => new(
                WorkerActivityPhases.SubProfile,
                "сбор откликов",
                AccountAlphaId,
                "user_01",
                "sp-main",
                "Основной",
                null,
                Now.AddSeconds(-8)),
            1 => new(
                WorkerActivityPhases.Waiting,
                "ожидание следующего цикла",
                null,
                null,
                null,
                null,
                Now.AddMinutes(4),
                Now.AddSeconds(-12)),
            2 => new(
                WorkerActivityPhases.Cycle,
                "старт цикла мониторинга",
                null,
                null,
                null,
                null,
                null,
                Now.AddSeconds(-5)),
            10 => new(
                WorkerActivityPhases.Account,
                "проверка авторизации",
                AccountBetaId,
                "user_02",
                null,
                null,
                null,
                Now.AddHours(-1)),
            _ => new(
                WorkerActivityPhases.Idle,
                "ожидание",
                null,
                null,
                null,
                null,
                null,
                Now.AddMinutes(-2))
        };
    }

    public static DashboardViewModel BuildDashboardViewModel(DashboardPeriod? period = null, IOfficeContext? officeContext = null)
    {
        period ??= DashboardPeriod.Today;
        officeContext ??= new OfficeContext();
        var updatedAt = Now;
        var activityChart = DashboardChartsBuilder.FromHourlyActivity(BuildHourly());
        var hourlyChart = DashboardChartsBuilder.ToResponsePoints(activityChart);
        var previewResponses = HourlyResponsesGenerator.DailyValues.ToList();
        var previewSent = previewResponses.Select(v => Math.Max(0, (int)Math.Round(v * 0.89))).ToList();
        var previewDuplicates = previewResponses.Select(v => Math.Max(0, v / 5)).ToList();
        var previewErrors = previewResponses.Select((v, i) => i == 14 ? 2 : (i % 9 == 0 ? 1 : 0)).ToList();
        var accountStats = new AccountStatsViewModel
        {
            Total = 30,
            Active = 24,
            Inactive = 5,
            Blocked = 0,
            Errors = 1
        };
        var kpiCards = (IReadOnlyList<DashboardKpiCardViewModel>)
        [
            new()
                {
                    Key = "responses",
                    Href = KpiCardLinks.Dashboard("responses", period.From, period.To),
                    Label = "Откликов всего",
                    Value = "1234",
                    CountValue = 1234,
                    Delta = "+12.4%",
                    DeltaTone = "good",
                    IconClass = "fa-regular fa-comments",
                    IconTone = "blue",
                    Sparkline = SparklineGenerator.FromSeries(previewResponses),
                    SparkColor = "#2563eb"
                },
                new()
                {
                    Key = "sent",
                    Href = KpiCardLinks.Dashboard("sent", period.From, period.To),
                    Label = "Отправленные",
                    Value = "1100",
                    CountValue = 1100,
                    Delta = "89.1%",
                    DeltaTone = "good",
                    IconClass = "fa-solid fa-paper-plane",
                    IconTone = "green",
                    Sparkline = SparklineGenerator.FromSeries(previewSent),
                    SparkColor = "#15803d"
                },
                new()
                {
                    Key = "duplicates",
                    Href = KpiCardLinks.Dashboard("duplicates", period.From, period.To),
                    Label = "Дублей",
                    Value = "256",
                    CountValue = 256,
                    Delta = "-5.3%",
                    DeltaTone = "good",
                    IconClass = "fa-regular fa-clone",
                    IconTone = "green",
                    Sparkline = SparklineGenerator.FromSeries(previewDuplicates),
                    SparkColor = "#16a34a"
                },
                new()
                {
                    Key = "errors",
                    Href = KpiCardLinks.Dashboard("errors", period.From, period.To),
                    Label = "Ошибок",
                    Value = "18",
                    CountValue = 18,
                    Delta = "+2.1%",
                    DeltaTone = "bad",
                    IconClass = "fa-solid fa-triangle-exclamation",
                    IconTone = "orange",
                    Sparkline = SparklineGenerator.FromSeries(previewErrors),
                    SparkColor = "#f59e0b"
                },
                new()
                {
                    Key = "accounts",
                    Href = KpiCardLinks.Dashboard("accounts", period.From, period.To),
                    Label = "Аккаунтов активно",
                    Value = "24 / 30",
                    CountValue = 24,
                    ValueSuffix = " / 30",
                    Delta = "80%",
                    DeltaTone = "good",
                    IconClass = "fa-regular fa-user",
                    IconTone = "purple",
                    SparkColor = "#7c3aed",
                    Segments = DashboardChartsBuilder.BuildAccountSegments(accountStats)
                },
                new()
                {
                    Key = "workers",
                    Href = KpiCardLinks.Dashboard("workers", period.From, period.To),
                    Label = "Воркеров онлайн",
                    Value = "3 / 3",
                    CountValue = 3,
                    ValueSuffix = " / 3",
                    Delta = "100%",
                    DeltaTone = "good",
                    IconClass = "fa-solid fa-server",
                    IconTone = "blue",
                    SparkColor = "#2563eb",
                    Segments = DashboardChartsBuilder.BuildWorkerSegments(3, 3)
                }
        ];

        return new DashboardViewModel
        {
            Header = new PageHeaderViewModel
            {
                Title = "Панель управления",
                Subtitle = "Общая сводка по всем воркерам",
                ShowRefresh = true,
                ShowDateRange = true,
                UpdatedAtUtc = updatedAt,
                DateRangeLabel = period.Label,
                DateFrom = period.From,
                DateTo = period.To,
                ActivePeriodPreset = period.ActivePreset
            },
            KpiCards = kpiCards,
            Workers = FilterWorkerRowsByOffice(BuildWorkerRows(), officeContext.EffectiveOfficeId).Take(3).Select(w => new DashboardWorkerRowViewModel
            {
                Id = w.Id,
                DisplayName = w.DisplayName,
                MachineName = w.MachineName,
                IsOnline = w.IsOnline,
                IsEnabled = w.IsEnabled,
                ActiveAccounts = w.ActiveAccounts,
                TotalAccounts = w.TotalAccounts,
                Responses = w.Responses,
                Duplicates = w.Duplicates,
                Errors = w.Errors,
                LastActivityUtc = w.LastActivityUtc,
                CurrentActivityLabel = w.CurrentActivityLabel,
                CurrentActivityTone = w.CurrentActivityTone,
                IsActivityLive = w.IsActivityLive,
                OfficeName = w.OfficeName
            }).ToList(),
            HourlyChart = hourlyChart,
            Events = Events
                .Where(e => e.CreatedAtUtc >= DashboardRecentEvents.SinceUtc)
                .OrderByDescending(e => e.CreatedAtUtc)
                .Take(DashboardRecentEvents.Limit)
                .Select(DashboardEventMapper.Map)
                .ToList(),
            AccountStats = accountStats,
            Charts = DashboardChartsBuilder.FromPresentation(kpiCards, activityChart, accountStats),
            ShowOfficeColumn = officeContext.ShowOfficeColumn,
            EnabledWorkersCount = FilterWorkerRowsByOffice(BuildWorkerRows(), officeContext.EffectiveOfficeId).Count(w => w.IsEnabled),
            DisabledWorkersCount = FilterWorkerRowsByOffice(BuildWorkerRows(), officeContext.EffectiveOfficeId).Count(w => !w.IsEnabled),
            ShowWorkersMonitoringControls = FilterWorkerRowsByOffice(BuildWorkerRows(), officeContext.EffectiveOfficeId).Count > 0
        };
    }

    public static WorkerDetail? GetWorker(Guid id)
    {
        if (id == WorkerMoscowId)
        {
            return new WorkerDetail(
                WorkerMoscowId, "Worker #1", "WIN-W01", "2.4.1",
                "Running", null, true, true, Now.AddSeconds(-12), Now.AddMinutes(8),
                new DashboardStatsDto(14, 432, 334, 42, 98, 5, 0, 10, 0, 0, 120, 0, 2, BuildHourly(), BuildWeekly()),
                BuildWorkerBalances(WorkerMoscowId),
                MaxConcurrentAccounts: 3,
                AdsPowerApiBaseUrl: "http://local.adspower.net:50325",
                AdsPowerApiKey: "preview-adspower-key",
                TodayResponses: 432,
                TodayDuplicates: 98,
                TodayErrors: 5,
                ActiveAccountCount: 8,
                TotalAccountCount: 10,
                CurrentActivity: BuildPreviewWorkerActivity(0, true),
                ResponseHighlightEnabled: true,
                ResponseHighlightAgeBuckets: "63+",
                AutoScheduleEnabled: true,
                AutoScheduleDays: "Mon,Tue,Wed,Thu,Fri",
                AutoScheduleFromLocalTime: "07:00",
                AutoScheduleToLocalTime: "19:00");
        }

        if (id == WorkerSpbId)
        {
            return new WorkerDetail(
                WorkerSpbId, "Worker #2", "WIN-W02", "2.4.1",
                "Running", null, true, true, Now.AddMinutes(-5), Now.AddMinutes(5),
                new DashboardStatsDto(9, 62, 48, 5, 3, 2, 1, 3, 1, 1, 15, 2, 1, BuildHourly(), BuildWeekly()),
                [new(AccountGammaId, "avito_gamma", 67_400m, [new("Основной", 67_400m)])],
                CurrentActivity: BuildPreviewWorkerActivity(1, true));
        }

        if (id == WorkerKazanId)
        {
            return new WorkerDetail(
                WorkerKazanId, "Worker #3", "WIN-W03", "2.4.1",
                "Running", null, true, true, Now.AddSeconds(-15), Now.AddMinutes(5),
                new DashboardStatsDto(3, 26, 16, 2, 2, 0, 0, 2, 0, 0, 9, 0, 0, BuildHourly(), BuildWeekly()),
                [],
                CurrentActivity: BuildPreviewWorkerActivity(2, true));
        }

        var row = BuildWorkerRows().FirstOrDefault(w => w.Id == id);
        if (row is null) return null;

        var index = Array.IndexOf(PreviewWorkerIds, row.Id);
        var machine = $"WIN-W{(index + 1):D2}";
        return new WorkerDetail(
            row.Id,
            row.DisplayName,
            machine,
            "2.4.1",
            row.IsOnline ? "Running" : "Stopped",
            row.IsOnline ? null : "Нет heartbeat",
            row.IsOnline,
            row.IsOnline,
            row.LastActivityUtc?.ToUniversalTime(),
            row.IsOnline ? Now.AddMinutes(5) : null,
            new DashboardStatsDto(
                6, row.Responses, row.Responses - row.Duplicates, row.Duplicates, row.Errors,
                0, 0, row.TotalAccounts, 0, 0, row.TotalAccounts, 0, 0, BuildHourly(), BuildWeekly()),
            [],
            TodayResponses: row.Responses,
            TodayDuplicates: row.Duplicates,
            TodayErrors: row.Errors,
            ActiveAccountCount: row.ActiveAccounts,
            TotalAccountCount: row.TotalAccounts,
            CurrentActivity: BuildPreviewWorkerActivity(index, row.IsOnline));
    }

    public static WorkerDetailsViewModel? BuildWorkerDetailsViewModel(
        Guid id,
        string? sort = null,
        string? sortDir = null)
    {
        var worker = GetWorker(id);
        if (worker is null) return null;

        var tableSort = TableSort.Parse(sort, sortDir, TableSort.WorkerAccounts.Default, TableSort.WorkerAccounts.Columns);
        var summary = BuildWorkerRows().FirstOrDefault(w => w.Id == id);
        return WorkerDetailsBuilder.Build(
            worker,
            TableSort.WorkerAccounts.Apply(GetWorkerAccountRows(id), tableSort).ToList(),
            GetWorkerEvents(id),
            GetWorkerMeta(id),
            summary,
            sort: tableSort);
    }

    private static IReadOnlyList<WorkerBalanceDto> BuildWorkerBalances(Guid workerId) =>
        GetAccounts(workerId)
            .Select(a => BuildDemoBalance(a.AccountId, a.DisplayName, a.SubProfiles))
            .ToList();

    private static WorkerExtraInfoViewModel GetWorkerMeta(Guid workerId)
    {
        var index = Array.IndexOf(PreviewWorkerIds, workerId);
        if (index < 0) index = 0;

        var row = BuildWorkerRows().FirstOrDefault(w => w.Id == workerId);
        var startedAt = Now.AddDays(-2).AddHours(-14).AddMinutes(-index * 17);
        return new WorkerExtraInfoViewModel
        {
            IpAddress = $"185.22.{174 + index}.{101 + index}",
            StartedAtUtc = startedAt,
            LeadFlowVersion = "2.4.1",
            AgentVersion = "1.8.3",
            OperatingSystem = index % 3 == 0 ? "Windows Server 2022" : index % 3 == 1 ? "Windows Server 2019" : "Ubuntu 22.04 LTS",
            ConnectionCheck = row?.IsOnline == true ? $"Успешно ({12 + index} мс)" : "Нет связи"
        };
    }

    private static IReadOnlyList<WorkerAccountRowViewModel> GetWorkerAccountRows(Guid workerId) =>
        GetAccounts(workerId)
            .Select(a =>
            {
                var balance = BuildWorkerBalances(workerId).FirstOrDefault(b => b.AccountId == a.AccountId);
                return WorkerDetailsBuilder.MapAccount(a, balance, workerId);
            })
            .ToList();

    private static IReadOnlyList<DashboardEventRowViewModel> GetWorkerEvents(Guid workerId) =>
        Events
            .Where(e => e.WorkerId == workerId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(10)
            .Select(DashboardEventMapper.Map)
            .ToList();

    private static string DemoAdsPowerProfileId(int seed) =>
        seed % 8 == 0 ? string.Empty : $"k19{seed:D4}";

    private static IReadOnlyList<WorkerSubProfileDto> BuildDemoSubProfiles(Guid accountId, int seed, string tone)
    {
        if (tone is "inactive" or "blocked")
        {
            return seed % 3 == 0
                ? [new($"sp-{seed}-main", "Основной", "Работа", true, 0m, null, null, null)]
                : [];
        }

        if (accountId == AccountAlphaId || seed == 1)
        {
            return
            [
                new("sp-main", "Основной", "Работа", true, 28_500m, null, null, null, true),
                new("sp-extra", "Доп.", "Работа", false, 12_800m, null, null, null, true),
                new("sp-arch", "Архив", "Личное", false, 900m, null, null, null, false)
            ];
        }

        if (accountId == AccountBetaId || seed == 2)
        {
            return
            [
                new("sp-beta-main", "Основной", "Работа", true, 11_200m, null, null, null, true),
                new("sp-beta-reserve", "Резерв", "Работа", false, 7_550m, null, null, null, true)
            ];
        }

        if (accountId == Guid.Parse("22222222-2222-2222-2222-222222222209") || seed == 3)
        {
            return
            [
                new("sp-auth-main", "Основной", "Работа", true, 31_200m, "AuthRequired", "Требуется повторный вход в Avito.", Now.AddHours(-2), true)
            ];
        }

        if (accountId == Guid.Parse("22222222-2222-2222-2222-222222222211") || seed == 5)
        {
            return
            [
                new("sp-5-main", "Основной", "Работа", true, 9_800m, null, null, null, true),
                new("sp-5-extra", "Доп.", "Работа", false, 4_200m, null, null, null, true),
                new("sp-5-old", "Старый", "Архив", false, 1_100m, null, null, null, false),
                new("sp-5-test", "Тест", "Тест", false, 700m, "Captcha", "Обнаружена капча при переключении.", Now.AddMinutes(-45), true)
            ];
        }

        if (accountId == Guid.Parse("22222222-2222-2222-2222-222222222214") || seed == 8)
        {
            return
            [
                new("sp-8-main", "Основной", "Работа", true, 24_100m, "Timeout", "Таймаут при сборе откликов.", Now.AddMinutes(-28), true),
                new("sp-8-extra", "Доп.", "Работа", false, 4_800m, null, null, null, true)
            ];
        }

        if (accountId == AccountGammaId)
        {
            return
            [
                new("sp-gamma-main", "Основной", "Работа", true, 42_300m, null, null, null, true),
                new("sp-gamma-b2b", "B2B", "Бизнес", false, 18_900m, null, null, null, true),
                new("sp-gamma-off", "Запасной", "Резерв", false, 6_200m, null, null, null, false)
            ];
        }

        return (seed % 4) switch
        {
            0 => [],
            1 => [new($"sp-{seed}-solo", "Основной", "Работа", true, 15_000m + seed * 250m, null, null, null, true)],
            _ =>
            [
                new($"sp-{seed}-a", "Основной", "Работа", true, 10_000m + seed * 180m, null, null, null, true),
                new($"sp-{seed}-b", "Доп.", "Работа", false, 3_500m + seed * 90m, null, null, null, seed % 2 == 0)
            ]
        };
    }

    private static WorkerBalanceDto BuildDemoBalance(Guid accountId, string accountName, IReadOnlyList<WorkerSubProfileDto>? subProfiles)
    {
        if (subProfiles is null || subProfiles.Count == 0)
        {
            return new(accountId, accountName, 0m, []);
        }

        var items = subProfiles
            .Select(sp => new SubProfileBalanceDto(sp.Name, sp.Balance))
            .ToList();
        var total = subProfiles
            .Where(sp => sp.Balance.HasValue)
            .Sum(sp => sp.Balance!.Value);

        return new(accountId, accountName, total, items);
    }

    private static WorkerAccountDto BuildDemoWorkerAccount(
        Guid accountId,
        string displayName,
        string status,
        bool isEnabled,
        int activeAds,
        int blocked,
        int drafts,
        string? lastError,
        DateTime? lastMonitoringAt,
        int seed,
        string tone,
        int todayResponses = 0,
        int todayDuplicates = 0,
        int todayErrors = 0,
        bool isEnabledInPanel = true,
        DateTime? subProfilesRefreshedAtUtc = null,
        DateTime? subProfilesRefreshRequestedAtUtc = null)
    {
        var subProfiles = BuildDemoSubProfiles(accountId, seed, tone);
        return new WorkerAccountDto(
            accountId,
            displayName,
            status,
            isEnabled,
            activeAds,
            blocked,
            drafts,
            lastError,
            lastMonitoringAt,
            isEnabledInPanel,
            DemoAdsPowerProfileId(seed),
            subProfiles.Count > 0 ? subProfiles : null,
            subProfilesRefreshedAtUtc ?? (subProfiles.Count > 0 ? Now.AddHours(-2) : null),
            subProfilesRefreshRequestedAtUtc,
            todayResponses,
            todayDuplicates,
            todayErrors);
    }

    public static IReadOnlyList<WorkerAccountDto> GetAccounts(Guid workerId)
    {
        if (workerId == WorkerMoscowId)
        {
            return
            [
                BuildDemoWorkerAccount(AccountAlphaId, "user_01", "Active", true, 12, 0, 1, null, Now.AddMinutes(-3), 1, "active", 58, 5, 0),
                BuildDemoWorkerAccount(AccountBetaId, "user_02", "Active", true, 8, 1, 0, null, Now.AddMinutes(-4), 2, "active", 51, 4, 0),
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222209"), "user_03", "RequiresLogin", true, 0, 0, 0, "Требуется повторный вход", Now.AddHours(-2), 3, "active", 0, 0, 0),
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222210"), "user_04", "Active", true, 10, 0, 0, null, Now.AddMinutes(-8), 4, "active", 47, 3, 0),
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222211"), "user_05", "Active", true, 9, 0, 0, null, Now.AddMinutes(-10), 5, "active", 44, 2, 0),
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222212"), "user_06", "Active", true, 7, 0, 0, null, Now.AddMinutes(-12), 6, "active", 39, 2, 0, subProfilesRefreshRequestedAtUtc: Now.AddMinutes(-3)),
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222213"), "user_07", "Active", true, 11, 0, 0, null, Now.AddMinutes(-14), 7, "active", 36, 1, 0),
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222214"), "user_08", "Error", true, 3, 1, 0, "Ошибка отправки в CRM", Now.AddMinutes(-16), 8, "active", 33, 1, 1),
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222215"), "user_09", "Active", true, 6, 0, 0, null, Now.AddMinutes(-18), 9, "active", 41, 2, 0),
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222216"), "user_10", "Active", true, 5, 0, 0, null, Now.AddMinutes(-20), 10, "active", 38, 1, 0)
            ];
        }

        if (workerId == WorkerSpbId)
        {
            return
            [
                BuildDemoWorkerAccount(AccountGammaId, "avito_gamma", "Active", true, 15, 2, 1, null, Now.AddMinutes(-6), 11, "active", 22, 3, 0),
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222205"), "avito_epsilon", "Error", true, 3, 1, 0, "Ошибка отправки в CRM", Now.AddMinutes(-12), 12, "active", 8, 1, 1),
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222206"), "avito_zeta", "Paused", false, 0, 0, 0, null, Now.AddDays(-1), 13, "inactive", 0, 0, 0, isEnabledInPanel: false)
            ];
        }

        if (workerId == WorkerKazanId)
        {
            return
            [
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222207"), "avito_eta", "Offline", false, 0, 0, 0, null, Now.AddMinutes(-18), 14, "inactive", 0, 0, 0, isEnabledInPanel: false),
                BuildDemoWorkerAccount(Guid.Parse("22222222-2222-2222-2222-222222222208"), "avito_theta", "Offline", false, 0, 0, 0, null, Now.AddMinutes(-18), 15, "blocked", 0, 0, 0, isEnabledInPanel: false)
            ];
        }

        return [];
    }

    public static IReadOnlyList<WorkerEventListItem> Events =>
    [
        new(Guid.Parse("33333333-3333-3333-3333-333333333301"), WorkerMoscowId, "VDS-Москва-01", AccountAlphaId, "user_01", "Info", "Отклик отправлен в CRM", null, Now.AddMinutes(-1)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333302"), WorkerSpbId, "VDS-СПб-02", AccountGammaId, "avito_gamma", "Warning", "Дубликат отклика пропущен", "candidate_id=88421", Now.AddMinutes(-4)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333303"), WorkerMoscowId, "VDS-Москва-01", AccountBetaId, "user_02", "Info", "Мониторинг завершён", "3 аккаунта", Now.AddMinutes(-7)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333304"), WorkerSpbId, "VDS-СПб-02", Guid.Parse("22222222-2222-2222-2222-222222222205"), "avito_epsilon", "Error", "Ошибка отправки в CRM", "HTTP 503", Now.AddMinutes(-12)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333305"), WorkerKazanId, "VDS-Казань-03", null, null, "Warning", "Heartbeat не получен", "18 мин", Now.AddMinutes(-18)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333306"), WorkerMoscowId, "VDS-Москва-01", AccountAlphaId, "user_01", "Info", "Новый отклик получен", "vacancy_id=120984", Now.AddMinutes(-22)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333307"), WorkerSpbId, "VDS-СПб-02", AccountGammaId, "avito_gamma", "Info", "Баланс обновлён", "67400 ₽", Now.AddMinutes(-35)),
        new(Guid.Parse("33333333-3333-3333-3333-333333333308"), WorkerMoscowId, "VDS-Москва-01", Guid.Parse("22222222-2222-2222-2222-222222222204"), "user_04", "Warning", "Требуется авторизация", null, Now.AddHours(-2))
    ];

    private static IReadOnlyList<DailyResponseBucketDto> BuildStatisticsDailyTrend(
        DateTime fromDate,
        int days,
        GlobalDashboardSummary summary)
    {
        var weights = Enumerable.Range(0, days)
            .Select(i => 0.75 + (i % 5) * 0.08 + ((i * 3) % 7) * 0.02)
            .ToList();
        var weightSum = Math.Max(0.01, weights.Sum());

        return weights
            .Select((weight, index) =>
            {
                var total = Math.Max(1, (int)Math.Round(summary.TotalToday * weight / weightSum));
                var sent = Math.Max(0, Math.Min(total, (int)Math.Round(summary.SentToCrm * weight / weightSum)));
                var inProgress = Math.Max(0, Math.Min(total - sent, (int)Math.Round(summary.InProgress * weight / weightSum)));
                var duplicates = Math.Max(0, Math.Min(total - sent - inProgress, (int)Math.Round(summary.Duplicates * weight / weightSum)));
                var errors = Math.Max(0, total - sent - inProgress - duplicates);
                return new DailyResponseBucketDto(
                    fromDate.AddDays(index),
                    total,
                    sent,
                    inProgress,
                    0,
                    duplicates,
                    errors);
            })
            .ToList();
    }

    private static IReadOnlyList<ActivityPointDto> BuildHourly()
    {
        var profile = HourlyResponsesGenerator.DailyValues;
        var points = new List<ActivityPointDto>(profile.Count);
        for (var h = 0; h < profile.Count; h++)
        {
            var value = profile[h];
            points.Add(new ActivityPointDto(
                $"{h:00}:00",
                value,
                Math.Max(0, value - 2),
                h == 11 ? 2 : 0,
                h == 14 ? 1 : 0,
                h,
                1,
                DateTime.Today.AddHours(Math.Min(h, 23))));
        }

        return points;
    }

    private static IReadOnlyList<ActivityPointDto> BuildWeekly()
    {
        var days = new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" };
        var counts = new[] { 142, 168, 155, 184, 176, 98, 64 };
        return days.Select((label, i) => new ActivityPointDto(
            label,
            counts[i],
            counts[i] - 10,
            4,
            i == 3 ? 3 : 1,
            0,
            24,
            DateTime.Today.AddDays(-6 + i))).ToList();
    }

    public static ErrorsIndexViewModel BuildErrorsIndexViewModel(
        ErrorsFilterViewModel filters,
        int page,
        int pageSize,
        string? sort = null,
        string? sortDir = null) =>
        ErrorsIndexBuilder.Build(BuildPreviewErrorRows(), filters, page, pageSize, sort, sortDir);

    private static IReadOnlyList<ErrorRowViewModel> BuildPreviewErrorRows()
    {
        const int total = 256;
        var rng = new Random(5150);
        var rows = new List<ErrorRowViewModel>(total);
        var severityPlan = new List<string>(total);

        severityPlan.AddRange(Enumerable.Repeat("critical", 20));
        severityPlan.AddRange(Enumerable.Repeat("high", 56));
        severityPlan.AddRange(Enumerable.Repeat("medium", 115));
        severityPlan.AddRange(Enumerable.Repeat("low", 65));

        for (var i = severityPlan.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (severityPlan[i], severityPlan[j]) = (severityPlan[j], severityPlan[i]);
        }

        var types = new[] { "auth", "bitrix", "network", "balance", "blocked", "parsing", "postgres", "api", "unknown" };
        var messagesByType = new Dictionary<string, string[]>
        {
            ["auth"] = ["Неверный логин или пароль", "Сессия аккаунта истекла", "Требуется повторная авторизация"],
            ["bitrix"] = ["Ошибка отправки сообщения в Bitrix24", "CRM вернула код 503", "Не удалось создать лид в Bitrix24"],
            ["network"] = ["Таймаут соединения с API", "Сеть недоступна", "Ошибка DNS при обращении к серверу"],
            ["balance"] = ["Не удалось получить баланс аккаунта", "Ошибка обновления баланса"],
            ["blocked"] = ["Аккаунт заблокирован на Avito", "Объявления аккаунта заблокированы"],
            ["parsing"] = ["Ошибка парсинга ответа Avito", "Некорректный формат HTML-страницы"],
            ["postgres"] = ["Не удалось подключиться к PostgreSQL", "Ошибка записи в базу данных"],
            ["api"] = ["API вернул код 500", "Недопустимый ответ API"],
            ["unknown"] = ["Неизвестная ошибка обработки", "Непредвиденное исключение в агенте"]
        };

        for (var i = 0; i < total; i++)
        {
            var severity = severityPlan[i];
            var errorType = types[i % types.Length];
            var workerIndex = i % PreviewWorkerIds.Length;
            var workerId = PreviewWorkerIds[workerIndex];
            var workerName = $"Worker #{workerIndex + 1}";
            var accountName = $"user_{(i % 120) + 1:D2}";
            var accountId = Guid.Parse($"33333333-3333-3333-3333-{(i % 120) + 1:D12}");
            var messages = messagesByType[errorType];
            var message = messages[i % messages.Length];
            var firstSeenMinutes = 180 + i * 7 + rng.Next(0, 20);
            var lastSeenMinutes = rng.Next(1, Math.Max(2, firstSeenMinutes / 3));
            var occurredAt = Now.AddMinutes(-firstSeenMinutes);
            var lastSeen = Now.AddMinutes(-lastSeenMinutes);
            var occurrences = severity switch
            {
                "critical" => rng.Next(12, 150),
                "high" => rng.Next(5, 80),
                "medium" => rng.Next(2, 40),
                _ => rng.Next(1, 15)
            };

            rows.Add(new ErrorRowViewModel
            {
                Id = Guid.Parse($"55555555-5555-5555-5555-{(i + 1):D12}"),
                OccurredAtUtc = occurredAt,
                Severity = severity,
                SeverityLabel = ErrorsIndexBuilder.SeverityLabel(severity),
                ErrorType = errorType,
                ErrorTypeLabel = ErrorsIndexBuilder.ErrorTypeLabel(errorType),
                Message = message,
                CopyText = message,
                AccountName = accountName,
                AccountId = accountId,
                WorkerId = workerId,
                WorkerName = workerName,
                OccurrenceCount = occurrences,
                LastSeenUtc = lastSeen
            });
        }

        return rows;
    }

    public static EventsIndexViewModel BuildEventsIndexViewModel(
        EventsFilterViewModel filters,
        int page,
        int pageSize,
        string? journalView = null,
        string? sort = null,
        string? sortDir = null) =>
        EventsIndexBuilder.Build(
            BuildPreviewEventRows(),
            filters,
            page,
            pageSize: pageSize,
            journalView: journalView,
            sort: sort,
            sortDir: sortDir);

    private static IReadOnlyList<EventRowViewModel> BuildPreviewEventRows()
    {
        const int total = 1254;
        var rng = new Random(9091);
        var rows = new List<EventRowViewModel>(total);
        var levelPlan = new List<string>(total);

        levelPlan.AddRange(Enumerable.Repeat("success", 752));
        levelPlan.AddRange(Enumerable.Repeat("info", 251));
        levelPlan.AddRange(Enumerable.Repeat("warning", 150));
        levelPlan.AddRange(Enumerable.Repeat("error", 101));

        for (var i = levelPlan.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (levelPlan[i], levelPlan[j]) = (levelPlan[j], levelPlan[i]);
        }

        var responseMessages = new[]
        {
            "Получен новый отклик на объявление №{0}",
            "Новый отклик отправлен в CRM",
            "Отклик успешно обработан"
        };
        var duplicateMessages = new[]
        {
            "Найден дубликат отклика",
            "Дубликат отклика пропущен"
        };
        var errorMessages = new[]
        {
            "Ошибка отправки в CRM",
            "Ошибка авторизации аккаунта",
            "Ошибка при отправке в Bitrix24"
        };
        var authMessages = new[]
        {
            "Аккаунт успешно авторизован",
            "Требуется повторная авторизация"
        };
        var balanceMessages = new[]
        {
            "Баланс обновлён",
            "Обновление баланса завершено"
        };
        var startMessages = new[] { "Воркер запущен", "Мониторинг запущен" };
        var stopMessages = new[] { "Heartbeat не получен", "Воркер остановлен" };
        var infoMessages = new[] { "Мониторинг завершён", "Плановая проверка выполнена", "Конфигурация обновлена" };

        for (var i = 0; i < total; i++)
        {
            var level = levelPlan[i];
            var workerIndex = i % PreviewWorkerIds.Length;
            var workerId = PreviewWorkerIds[workerIndex];
            var workerName = $"Worker #{workerIndex + 1}";
            var accountName = $"user_{(i % 120) + 1:D2}";
            var accountId = Guid.Parse($"33333333-3333-3333-3333-{(i % 120) + 1:D12}");
            var minutesAgo = i * 3 + rng.Next(0, 5);
            var occurredAt = Now.AddMinutes(-minutesAgo);

            string message;
            string? details = null;
            string type;

            if (level == "warning" && i % 2 == 0)
            {
                message = duplicateMessages[i % duplicateMessages.Length];
                details = $"candidate_id={rng.Next(10000, 99999)}";
                type = "duplicate";
            }
            else if (level == "error")
            {
                message = errorMessages[i % errorMessages.Length];
                details = i % 2 == 0 ? "HTTP 503" : null;
                type = "error";
            }
            else if (level == "success" && i % 3 == 0)
            {
                message = string.Format(responseMessages[i % responseMessages.Length], rng.Next(10000000, 99999999));
                type = "response";
            }
            else if (i % 17 == 0)
            {
                message = authMessages[i % authMessages.Length];
                type = level == "warning" ? "auth" : "auth";
                if (level == "warning") level = "warning";
            }
            else if (i % 19 == 0)
            {
                message = balanceMessages[i % balanceMessages.Length];
                details = $"{rng.Next(5, 98) * 1000} ₽";
                type = "balance";
                level = "info";
            }
            else if (i % 23 == 0)
            {
                message = startMessages[i % startMessages.Length];
                type = "start";
            }
            else if (i % 29 == 0)
            {
                message = stopMessages[i % stopMessages.Length];
                type = "stop";
                level = "warning";
            }
            else
            {
                message = level == "success"
                    ? responseMessages[i % responseMessages.Length].Contains('{')
                        ? string.Format(responseMessages[i % responseMessages.Length], rng.Next(10000000, 99999999))
                        : responseMessages[i % responseMessages.Length]
                    : infoMessages[i % infoMessages.Length];
                type = level == "success" ? "response" : "info";
            }

            var (typeLabel, typeIcon, typeTone) = EventsIndexBuilder.EventTypePresentation(type);
            var description = details is null ? message : $"{message} — {details}";

            rows.Add(new EventRowViewModel
            {
                Id = Guid.Parse($"44444444-4444-4444-4444-{(i + 1):D12}"),
                OccurredAtUtc = occurredAt,
                EventType = type,
                EventTypeLabel = typeLabel,
                EventTypeIcon = typeIcon,
                EventTypeTone = typeTone,
                Level = level,
                LevelLabel = level switch
                {
                    "error" => "Ошибка",
                    "warning" => "Предупреждение",
                    "info" => "Информация",
                    _ => "Успех"
                },
                AccountName = accountName,
                AccountId = accountId,
                WorkerId = workerId,
                WorkerName = workerName,
                Description = description,
                CopyText = description
            });
        }

        return rows;
    }

    public static AccountsIndexViewModel BuildAccountsIndexViewModel(
        string? searchQuery,
        string? tab,
        int page,
        int pageSize,
        string? sort = null,
        string? sortDir = null,
        bool showOfficeColumn = false,
        Guid? workerId = null) =>
        AccountsIndexBuilder.Build(
            BuildPreviewAccountRows(),
            searchQuery,
            tab,
            page,
            sort,
            sortDir,
            pageSize,
            showOfficeColumn,
            workerId: workerId,
            workers: ResponsesIndexBuilder.BuildWorkerOptions(GetWorkers(null)));

    private static IReadOnlyList<AccountRowViewModel> BuildPreviewAccountRows()
    {
        const int total = 120;
        var rng = new Random(4242);
        var rows = new List<AccountRowViewModel>(total);

        for (var i = 1; i <= total; i++)
        {
            var workerIndex = (i - 1) % PreviewWorkerIds.Length;
            var workerId = PreviewWorkerIds[workerIndex];
            var workerName = $"Worker #{workerIndex + 1}";
            var officeName = workerIndex >= 6 ? "Сибирь" : "Основной";

            var tone = i switch
            {
                <= 98 => "active",
                <= 110 => "inactive",
                <= 116 => "blocked",
                _ => "error"
            };

            var responses = tone == "inactive" ? 0 : rng.Next(8, 64);
            var errors = tone == "error" ? rng.Next(1, 5) : tone == "active" ? rng.Next(0, 2) : 0;
            var unique = Math.Max(0, responses - rng.Next(0, Math.Max(1, responses / 4)));
            var balance = tone is "inactive" or "blocked" ? 0m : rng.Next(400, 9800) * 10m + rng.Next(0, 9) * 100m + 50m;
            var lastActivity = tone switch
            {
                "inactive" => (DateTime?)null,
                "blocked" => Now.AddDays(-rng.Next(2, 14)),
                "error" => Now.AddMinutes(-rng.Next(30, 240)),
                _ => Now.AddMinutes(-rng.Next(1, 180))
            };

            var accountId = Guid.Parse($"33333333-3333-3333-3333-{i:D12}");
            var accountStatus = tone switch
            {
                "inactive" => "Paused",
                "blocked" => "Blocked",
                "error" => "Error",
                _ => "Active"
            };
            var accountDto = BuildDemoWorkerAccount(
                accountId,
                $"user_{i:D2}",
                accountStatus,
                tone != "inactive",
                tone == "active" ? rng.Next(4, 14) : 0,
                tone == "blocked" ? 1 : 0,
                0,
                tone == "error" ? "Ошибка мониторинга" : null,
                lastActivity,
                i,
                tone,
                responses,
                responses - unique,
                errors,
                tone != "inactive" && tone != "blocked",
                subProfilesRefreshRequestedAtUtc: i == 6 ? Now.AddMinutes(-4) : null);
            var balanceDetail = BuildDemoBalance(accountId, accountDto.DisplayName, accountDto.SubProfiles);
            var workerIsOnline = workerIndex < 3;
            var isProcessing = i == 1 && workerIndex == 0;
            WorkerActivityDto? activity = isProcessing
                ? new(
                    WorkerActivityPhases.SubProfile,
                    "сбор откликов",
                    accountId,
                    accountDto.DisplayName,
                    "sp-main",
                    "Основной",
                    null,
                    Now.AddSeconds(-8))
                : null;

            rows.Add(AccountsIndexBuilder.MapAccount(
                accountDto,
                workerId,
                workerName,
                officeName,
                balanceDetail.TotalBalance > 0 ? balanceDetail.TotalBalance : balance,
                balanceDetail,
                activity,
                workerIsOnline));
        }

        return rows;
    }

    public static IReadOnlyList<PanelUserDto> PanelUsers =>
    [
        new("preview-admin", "admin@orbita.local", true, PanelRoles.Admin, false, FullName: "Администратор Орбита"),
        new("preview-office-lead", "lead@orbita.local", true, PanelRoles.OfficeLead, false, PreviewOfficeId, "Основной", "Марина Ковалёва"),
        new(PreviewManagerElena, "elena@orbita.local", true, PanelRoles.Manager, false, PreviewOfficeId, "Основной", "Елена Воронцова"),
        new(PreviewManagerIgor, "igor@orbita.local", true, PanelRoles.SeniorManager, false, PreviewOfficeId, "Основной", "Игорь Савельев"),
        new("preview-operator", "operator@orbita.local", true, PanelRoles.Operator, true, PreviewOfficeId, "Основной", "Алексей Селезнёв")
    ];

    private static readonly object OfficeStaffSync = new();
    private static List<PanelUserDto>? _previewOfficeStaff;

    private static List<PanelUserDto> PreviewOfficeStaff
    {
        get
        {
            lock (OfficeStaffSync)
            {
                return _previewOfficeStaff ??= PanelUsers
                    .Where(x => x.OfficeId == PreviewOfficeId && OfficeStaffRules.IsAssignableRole(x.Role))
                    .ToList();
            }
        }
    }

    public static IReadOnlyList<PanelUserDto> GetOfficeStaffUsers(Guid? officeId)
    {
        var oid = officeId ?? PreviewOfficeId;
        lock (OfficeStaffSync)
        {
            return PreviewOfficeStaff
                .Where(x => x.OfficeId == oid)
                .OrderBy(x => x.Role == PanelRoles.SeniorManager ? 0 : 1)
                .ThenBy(x => x.FullName ?? x.Email, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public static (bool Success, string? Error) CreateOfficeStaffUser(
        string email,
        string fullName,
        string password,
        string role,
        Guid? officeId)
    {
        _ = password;
        var roleError = OfficeStaffRules.ValidateAssignableRole(role);
        if (roleError is not null)
        {
            return (false, roleError);
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(fullName))
        {
            return (false, "Email и ФИО обязательны.");
        }

        var oid = officeId ?? PreviewOfficeId;
        var officeName = Offices.FirstOrDefault(o => o.Id == oid)?.Name ?? "Офис";
        lock (OfficeStaffSync)
        {
            if (PreviewOfficeStaff.Any(x => string.Equals(x.Email, email.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "Пользователь с таким email уже существует.");
            }

            PreviewOfficeStaff.Add(new PanelUserDto(
                $"preview-staff-{Guid.NewGuid():N}"[..28],
                email.Trim(),
                true,
                PanelRoles.Normalize(role),
                false,
                oid,
                officeName,
                fullName.Trim()));
        }

        return (true, null);
    }

    public static (bool Success, string? Error) UpdateOfficeStaffFullName(string userId, string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Trim().Length > 256)
        {
            return (false, "ФИО обязательно и не должно превышать 256 символов.");
        }

        lock (OfficeStaffSync)
        {
            var idx = PreviewOfficeStaff.FindIndex(x => x.Id == userId);
            if (idx < 0)
            {
                return (false, "Пользователь не найден.");
            }

            var u = PreviewOfficeStaff[idx];
            PreviewOfficeStaff[idx] = u with { FullName = fullName.Trim() };
        }

        return (true, null);
    }

    public static (bool Success, string? Error) UpdateOfficeStaffRole(string userId, string role)
    {
        var roleError = OfficeStaffRules.ValidateAssignableRole(role);
        if (roleError is not null)
        {
            return (false, roleError);
        }

        lock (OfficeStaffSync)
        {
            var idx = PreviewOfficeStaff.FindIndex(x => x.Id == userId);
            if (idx < 0)
            {
                return (false, "Пользователь не найден.");
            }

            var u = PreviewOfficeStaff[idx];
            PreviewOfficeStaff[idx] = u with { Role = PanelRoles.Normalize(role) };
        }

        return (true, null);
    }

    public static (bool Success, string? Error) SetOfficeStaffLocked(string userId, bool locked)
    {
        lock (OfficeStaffSync)
        {
            var idx = PreviewOfficeStaff.FindIndex(x => x.Id == userId);
            if (idx < 0)
            {
                return (false, "Пользователь не найден.");
            }

            var u = PreviewOfficeStaff[idx];
            PreviewOfficeStaff[idx] = u with { IsLocked = locked };
        }

        return (true, null);
    }

    public static (bool Success, string? Error) DeleteOfficeStaffUser(string userId)
    {
        lock (OfficeStaffSync)
        {
            var removed = PreviewOfficeStaff.RemoveAll(x => x.Id == userId);
            return removed > 0 ? (true, null) : (false, "Пользователь не найден.");
        }
    }

    public static PanelProfileDto PanelProfile =>
        new("admin@orbita.local", PanelRoles.Admin);

    public static IReadOnlyList<OfficeDto> Offices =>
    [
        new(PreviewOfficeId, "Основной", true, Now.AddDays(-30), 6, 1, BitrixValidationStatuses.Ok, "demo.bitrix24.ru"),
        new(PreviewOffice2Id, "Сибирь", true, Now.AddDays(-14), 6, 0, BitrixValidationStatuses.NotConfigured, null)
    ];

    public static IReadOnlyList<OfficeOptionDto> PreviewCrmOfficeOptions =>
        Offices.Select(o => new OfficeOptionDto(o.Id, o.Name, o.IsEnabled, CrmEnabled: true)).ToList();

    public static OfficeBitrixIntegrationDto OfficeBitrixIntegration =>
        new(
            PreviewOfficeId,
            "Основной",
            "https://demo.bitrix24.ru/rest/1/***/",
            "demo.bitrix24.ru",
            BitrixValidationStatuses.Ok,
            "Вебхук настроен корректно.",
            Now.AddHours(-2),
            Now.AddHours(-2),
            true);

    public static OfficeDetailDto? GetOfficeDetail(Guid? officeId)
    {
        if (officeId is not Guid id)
        {
            return null;
        }

        var office = Offices.FirstOrDefault(x => x.Id == id);
        return office is null
            ? null
            : new OfficeDetailDto(
                office.Id,
                office.Name,
                office.IsEnabled,
                BitrixTransmissionEnabled: true,
                office.CreatedAtUtc,
                RegistrationConfigured: true,
                MaskedRegistrationSecret: "••••••••a1b2",
                office.BitrixValidationStatus,
                office.Id == PreviewOfficeId ? "Вебхук настроен корректно." : null,
                office.Id == PreviewOfficeId ? "https://demo.bitrix24.ru/rest/1/***/" : null,
                office.BitrixPortalHost,
                office.Id == PreviewOfficeId ? Now.AddHours(-2) : null,
                CrmEnabled: true);
    }

    public static PasswordPolicyDto PasswordPolicy =>
        new(8, true, false, false, false, 1);

    public static BitrixIntegrationDto MyBitrixIntegration =>
        new(
            "preview-operator",
            "https://demo.bitrix24.ru/rest/1/***/",
            "demo.bitrix24.ru",
            BitrixValidationStatuses.Ok,
            "Вебхук настроен корректно.",
            Now.AddHours(-2),
            Now.AddHours(-2));

    public static BitrixWebhookValidationDto BitrixValidationOk =>
        new(
            BitrixValidationStatuses.Ok,
            "Всё в порядке: вебхук рабочий, CRM доступна, контакты и проверка дублей будут работать.",
            [
                new("format", "Ссылка на вебхук", BitrixValidationStepStatuses.Ok, "Ссылка выглядит правильно."),
                new("connectivity", "Связь с Bitrix24", BitrixValidationStepStatuses.Ok, "Портал отвечает."),
                new("scope", "Право CRM", BitrixValidationStepStatuses.Ok, "Право CRM включено."),
                new("crm_read", "Доступ к контактам", BitrixValidationStepStatuses.Ok, "Контакты в CRM читаются."),
                new("duplicate_check", "Проверка дублей", BitrixValidationStepStatuses.Ok, "Поиск дублей работает.")
            ]);

    public static readonly Guid PreviewBitrixInstanceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public static BitrixWorkforceSettingsDto PreviewBitrixWorkforceSettings =>
        new(
            PreviewBitrixInstanceId,
            BitrixWorkforceDistribution.ShadowMode,
            0,
            "Europe/Moscow",
            [101, 102, 103],
            [
                new(null, BitrixWorkforceDistribution.NewScenario, "NEW", "NEW", false, 0, true),
                new(null, BitrixWorkforceDistribution.MissedCallScenario, "UC_FU2T4L", "UC_FU2T4L", true, 1, true),
                new(null, BitrixWorkforceDistribution.MissedCallScenario, "UC_ENSD7E", "UC_FU2T4L", true, 2, true),
                new(null, BitrixWorkforceDistribution.SubstituteMissedCallScenario, "UC_6OQRTF", "UC_6OQRTF", true, 3, true),
                new(null, BitrixWorkforceDistribution.SubstituteMissedCallScenario, "UC_WT8KQY", "UC_6OQRTF", true, 4, true)
            ],
            480,
            660,
            120,
            50m,
            60,
            20,
            true,
            true,
            true,
            false,
            true,
            "https://api.orbitsu.ru/api/v1/integrations/bitrix/events/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "preview-member-id",
            Now.AddMinutes(-12),
            Now.AddHours(-1));

    public static IReadOnlyList<BitrixWorkforceAssignmentDto> PreviewBitrixWorkforceAssignments =>
    [
        new(
            Guid.Parse("ad000000-0000-0000-0000-000000000001"),
            PreviewBitrixInstanceId,
            20980,
            31001,
            BitrixWorkforceDistribution.NewScenario,
            BitrixWorkforceDistribution.ShadowMode,
            "NEW",
            "NEW",
            101,
            102,
            BitrixWorkforceDecisions.Assigned,
            "Shadow: следующим по round-robin выбран менеджер 102.",
            Now.AddMinutes(-12),
            null,
            null,
            null,
            null),
        new(
            Guid.Parse("ad000000-0000-0000-0000-000000000002"),
            PreviewBitrixInstanceId,
            20976,
            null,
            BitrixWorkforceDistribution.MissedCallScenario,
            BitrixWorkforceDistribution.ShadowMode,
            "UC_ENSD7E",
            "UC_FU2T4L",
            103,
            null,
            BitrixWorkforceDecisions.Reserved,
            "Часть утренней очереди зарезервирована для второго менеджера.",
            Now.AddMinutes(-18),
            null,
            null,
            null,
            null),
        new(
            Guid.Parse("ad000000-0000-0000-0000-000000000003"),
            PreviewBitrixInstanceId,
            20972,
            null,
            BitrixWorkforceDistribution.SubstituteMissedCallScenario,
            BitrixWorkforceDistribution.ShadowMode,
            "UC_WT8KQY",
            "UC_6OQRTF",
            101,
            null,
            BitrixWorkforceDecisions.Deferred,
            "Нет менеджеров с рабочим днём OPENED или PAUSED.",
            Now.AddMinutes(-24),
            null,
            null,
            null,
            null)
    ];

    private static readonly BitrixInstanceIntegrationSettingsDto PreviewIntegrationSettings = new(
        "Deal",
        1,
        "Авито",
        string.Empty,
        "UF_CRM_1777753181424",
        "UF_CRM_1777753209215",
        "UF_CRM_1777753293892",
        true);

    public static IReadOnlyList<BitrixInstanceListItemDto> PreviewBitrixInstances =>
    [
        new(
            PreviewBitrixInstanceId,
            "Основной",
            "B24-1",
            "demo.bitrix24.ru",
            BitrixValidationStatuses.Ok,
            "Вебхук настроен корректно.",
            true,
            40,
            12)
    ];

    public static BitrixInstanceDto? GetPreviewBitrixInstance(Guid id)
    {
        var listItem = PreviewBitrixInstances.FirstOrDefault(x => x.Id == id || id == Guid.Empty);
        if (listItem is null)
        {
            return null;
        }

        return new BitrixInstanceDto(
            listItem.Id,
            PreviewOfficeId,
            listItem.Name,
            listItem.Signature,
            "https://demo.bitrix24.ru/rest/1/***/",
            listItem.PortalHost,
            listItem.ValidationStatus,
            listItem.ValidationMessage,
            Now.AddHours(-2),
            listItem.IsEnabled,
            PreviewIntegrationSettings,
            listItem.LeadExportLimit,
            listItem.LeadExportSessionCount,
            Now.AddHours(-2),
            Now.AddDays(-7),
            Now.AddHours(-2));
    }

    public static BitrixCrmImportPreviewDto PreviewBitrixCrmImport => new(
        PreviewBitrixInstanceId,
        PreviewOfficeId,
        "b24-l7qyiy.bitrix24.ru",
        0,
        [
            new BitrixCrmImportStageSummaryDto("НДЗ", "UC_SAMPLE_NDZ", 1),
            new BitrixCrmImportStageSummaryDto("Анкета", "UC_SAMPLE_QUESTIONNAIRE", 2),
            new BitrixCrmImportStageSummaryDto("Переговоры", "UC_SAMPLE_NEGOTIATIONS", 1)
        ],
        [
            new BitrixCrmImportManagerMatchDto(12, "Елена Воронцова", "preview-manager-elena", "Елена Воронцова", true),
            new BitrixCrmImportManagerMatchDto(18, "Игорь Белов", "preview-manager-igor", "Игорь Белов", true)
        ],
        [
            new BitrixCrmImportDealPreviewDto(41769, "Александр Иванов", "+7 900 111-22-33", "Подольск", "Сварщик", "НДЗ", 12, "Елена Воронцова", "preview-manager-elena", "Елена Воронцова", 3, 1, BitrixCrmImportActions.Create, null, null),
            new BitrixCrmImportDealPreviewDto(41770, "Сергей Петров", "+7 900 222-33-44", "Тула", "Водитель", "Анкета", 18, "Игорь Белов", "preview-manager-igor", "Игорь Белов", 2, 2, BitrixCrmImportActions.Create, null, null),
            new BitrixCrmImportDealPreviewDto(41771, "Максим Соколов", "+7 900 333-44-55", "Казань", "Монтажник", "Анкета", 12, "Елена Воронцова", "preview-manager-elena", "Елена Воронцова", 1, 1, BitrixCrmImportActions.UpdateExisting, Guid.Parse("90000000-0000-0000-0000-000000000009"), "Найдена существующая карточка с тем же телефоном."),
            new BitrixCrmImportDealPreviewDto(41772, "Кандидат без телефона", string.Empty, "Омск", "Электрик", "Переговоры", 18, "Игорь Белов", "preview-manager-igor", "Игорь Белов", 0, 0, BitrixCrmImportActions.MissingPhone, null, "У сделки нет телефона в связанном контакте.")
        ],
        2,
        1,
        0,
        1);

    public static DistributionRouteDto PreviewDistributionRoute =>
        new(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            PreviewOfficeId,
            true,
            [
                new(
                    Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    null,
                    PreviewBitrixInstanceId,
                    "Основной",
                    "B24-1",
                    0,
                    120,
                    80,
                    false)
            ],
            Now.AddHours(-1));

    public static IReadOnlyList<BitrixIntegrationListItemDto> BitrixIntegrations =>
    [
        new("preview-admin", "admin@orbita.local", PanelRoles.Admin, null, BitrixValidationStatuses.NotConfigured, null, null),
        new(PreviewManagerElena, "elena@orbita.local", PanelRoles.Manager, "demo.bitrix24.ru", BitrixValidationStatuses.Ok, "Вебхук настроен корректно.", Now.AddHours(-1)),
        new("preview-operator", "operator@orbita.local", PanelRoles.Operator, "demo.bitrix24.ru", BitrixValidationStatuses.Ok, "Вебхук настроен корректно.", Now.AddHours(-2))
    ];

    public static WorkerRegistrationInfoDto WorkerRegistrationInfo =>
        new(true, "****demo", "config");

    public static WorkerReleaseListResponse WorkerReleases =>
        new(
            new WorkerReleaseInfoDto("1.0.0.2", "Исправления стабильности", 52_428_800, "demo-sha256", true, Now.AddHours(-1)),
            [
                new WorkerReleaseInfoDto("1.0.0.2", "Исправления стабильности", 52_428_800, "demo-sha256", true, Now.AddHours(-1)),
                new WorkerReleaseInfoDto("1.0.0.1", null, 51_200_000, "demo-sha256-old", false, Now.AddDays(-2))
            ]);

    public static IReadOnlyList<AdminWorkerListItemDto> AdminWorkers =>
    [
        new(WorkerMoscowId, "Москва-01", "WIN-M01", "1.0.0.1", true, true, Now.AddMinutes(-2), Now.AddDays(-14), Now.AddDays(-3), true, "1.0.0.2", PreviewOfficeId, "Основной"),
        new(WorkerSpbId, "СПб-02", "WIN-SPB02", "1.0.0.2", true, false, Now.AddHours(-2), Now.AddDays(-10), null, false, "1.0.0.2", PreviewOfficeId, "Основной"),
        new(WorkerKazanId, "Казань-03", "WIN-KZN03", "0.9.5", false, false, Now.AddDays(-1), Now.AddDays(-30), Now.AddDays(-7), true, "1.0.0.2", PreviewOfficeId, "Основной")
    ];

    public static ServiceLogsPageDto BuildServiceLogsPage(
        string? q,
        string? level,
        string? service,
        DateTime? date,
        int page,
        int pageSize = 50)
    {
        IEnumerable<ServiceLogEntryDto> rows =
        [
            new(Now.AddMinutes(-3), "Info", "Orbita.Web", "[SettingsService.GetIndexAsync]", "Settings page opened (users tab). Session validated, cached profile loaded, rendering 12 panel users with 2 pending role updates.", null, false),
            new(Now.AddMinutes(-12), "Warning", "Orbita.Api", "[Program.Login]", "Login failed: invalid password for demo@orbita.local from 192.168.1.44. Attempt 3 of 5 before temporary lockout.", null, false),
            new(Now.AddMinutes(-28), "Error", "Orbita.Api", "[TelemetryService.HeartbeatAsync]", "Worker heartbeat timeout for WIN-W03 after 30s. LastSeenAtUtc=2026-06-27T08:41:12Z, expected interval=15s. Scheduling retry 2/3 and marking worker as offline in dashboard cache.", "trace-demo-001", false),
            new(Now.AddMinutes(-45), "Error", "Orbita.Api", "[WorkerAdminService.RotateKeyAsync]", "Failed to rotate worker API key: database connection timeout after 30s.\nWorkerId=8f2c1a9b-4d3e-4f5a-9b0c-1d2e3f4a5b6c\nMachine=WIN-W03\nRetry scheduled in 60s.\nSystem.TimeoutException: Timeout during reading from stream\n   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(...)\n   at Orbita.Api.Services.WorkerAdminService.RotateKeyAsync(...)", "trace-demo-002", false),
            new(Now.AddHours(-1), "Info", "Orbita.Web", "[DashboardService.GetIndexAsync]", "Dashboard summary loaded: 4 workers online, 128 active leads, 3 errors in the last hour.", null, false),
            new(Now.AddHours(-2), "Debug", "Orbita.Api", "[ServiceLogsQueryService.SearchAsync]", "Service logs query completed in 42ms. Filters: level=(all), service=(all), date=today, q=(empty), page=1, pageSize=50, total=6.", null, false)
        ];

        if (!string.IsNullOrWhiteSpace(level))
        {
            rows = rows.Where(r => string.Equals(r.Level, level, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(service))
        {
            rows = rows.Where(r => string.Equals(r.Service, service, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            rows = rows.Where(r =>
                r.Message.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Source.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (r.TraceId?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = rows.ToList();
        var items = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new ServiceLogsPageDto(items, list.Count, page, pageSize);
    }

    public static ResponsesDeliverOptionsViewModel BuildResponsesDeliverOptions() =>
        new()
        {
            DeliveryOffices = Offices
                .Where(o => o.IsEnabled)
                .Select(o => new DeliveryOfficeOptionViewModel
                {
                    Id = o.Id,
                    Name = o.Name,
                    CrmEnabled = true
                })
                .ToList(),
            SendBitrixInstances = ResponsesIndexBuilder.MapSendBitrixInstances(PreviewBitrixInstances)
        };

    public static ResponsesIndexViewModel BuildResponsesIndexViewModel(
        ResponsesFilterViewModel filters,
        Guid? selectedId = null,
        int? pageSize = null,
        string? sort = null,
        string? sortDir = null)
    {
        var period = new DashboardPeriod(filters.DateFrom, filters.DateTo);
        var tableSort = TableSort.Parse(sort, sortDir, TableSort.Responses.Default, TableSort.Responses.Columns);
        var allRows = BuildPreviewResponseRows();
        var filtered = FilterPreviewResponseRows(allRows, filters, period);
        var sorted = TableSort.Responses.Apply(filtered, tableSort).ToList();
        var total = sorted.Count;
        var page = Math.Max(1, filters.Page);
        pageSize = ListPageSizeDefaults.Normalize(pageSize, ListPageSizeDefaults.Responses);
        var paged = sorted
            .Skip((page - 1) * pageSize.Value)
            .Take(pageSize.Value)
            .ToList();

        var duplicates = filtered.Count(r => r.Status == ResponseStatuses.Duplicate);
        var sent = filtered.Count(r => r.Status == ResponseStatuses.Sent);
        var unique = total - duplicates;
        var uniqueAuthors = filtered
            .Where(r => r.PersonId != Guid.Empty)
            .Select(r => r.PersonId)
            .Distinct()
            .Count();

        var summary = new ResponsesSummaryDto(total, unique, duplicates, sent, uniqueAuthors, 17);

        var accountOptions = ResponsesIndexBuilder.BuildAccountOptions(
            filtered
                .GroupBy(r => new { r.AccountId, r.AccountName })
                .Select(g => new ResponseFilterAccountDto(g.Key.AccountId, g.Key.AccountName))
                .ToList());

        return new ResponsesIndexViewModel
        {
            Header = PageHeaderBuilder.ResponsesList(period),
            Filters = filters,
            PeriodLabel = period.Label,
            ActivePeriodPreset = period.ActivePreset,
            KpiCards = ResponsesIndexBuilder.BuildKpiCards(summary, period.From, period.To, filters.WorkerId, filters.AccountId),
            Statuses = ResponsesIndexBuilder.StatusOptions,
            Workers = BuildPreviewWorkerOptions(),
            Accounts = accountOptions,
            BitrixDestinations = ResponsesIndexBuilder.BuildBitrixDestinationOptions(
                PreviewBitrixInstances,
                PreviewCrmOfficeOptions),
            Genders = ResponsesIndexBuilder.GenderOptions,
            Vacancies = ResponsesIndexBuilder.BuildVacancyOptions(
                filtered
                    .Where(r => !string.IsNullOrWhiteSpace(r.Vacancy))
                    .GroupBy(r => r.Vacancy)
                    .Select(g => new ResponseFilterVacancyDto(g.Key, g.Count()))
                    .OrderByDescending(x => x.Count)
                    .Take(20)
                    .ToList()),
            Responses = paged,
            SendBitrixInstances = BuildResponsesDeliverOptions().SendBitrixInstances,
            OfficeOptions = Offices.Select(o => new EventFilterOptionViewModel
            {
                Value = o.Id.ToString(),
                Label = o.Name
            }).ToList(),
            DeliveryOffices = BuildResponsesDeliverOptions().DeliveryOffices,
            Pagination = new PaginationViewModel
            {
                Page = page,
                PageSize = pageSize.Value,
                TotalItems = total
            },
            Selected = selectedId is Guid id
                ? BuildPreviewResponseDetail(allRows, id)
                : null,
            HasActiveFilters = ResponsesIndexBuilder.HasActiveFilters(filters, period),
            ActiveFilterChips = FilterChipsBuilder.ForResponses(
                filters,
                period,
                ResponsesIndexBuilder.StatusOptions,
                BuildPreviewWorkerOptions(),
                accountOptions,
                ResponsesIndexBuilder.BuildBitrixDestinationOptions(
                    PreviewBitrixInstances,
                    PreviewCrmOfficeOptions),
                ResponsesIndexBuilder.GenderOptions,
                pageSize.Value),
            Sort = tableSort
        };
    }

    private static IReadOnlyList<EventFilterOptionViewModel> BuildPreviewWorkerOptions()
    {
        var options = new List<EventFilterOptionViewModel> { new() { Value = "", Label = "Все воркеры" } };
        options.AddRange(PreviewWorkerIds.Select((id, index) => new EventFilterOptionViewModel
        {
            Value = id.ToString(),
            Label = $"Worker #{index + 1}"
        }));
        return options;
    }

    private static IReadOnlyList<ResponseRowViewModel> BuildPreviewResponseRows()
    {
        const int total = 1248;
        var rng = new Random(5150);
        var rows = new List<ResponseRowViewModel>(total);
        var names = new[]
        {
            "Иван Петров", "Мария Сидорова", "Алексей Козлов", "Елена Волкова", "Дмитрий Орлов",
            "", "Анна Морозова", "Сергей Лебедев", "Ольга Новикова", "Павел Соколов"
        };
        var vacancies = new[]
        {
            "Кровать двуспальная 180×200",
            "Диван угловой серый",
            "Шкаф-купе 240 см",
            "Стол письменный белый",
            "Кухонный гарнитур 2.4 м"
        };
        var cities = new[] { "Москва", "Санкт-Петербург", "Казань", "Новосибирск", "Екатеринбург", "" };
        var statusPlan = new List<string>(total);
        statusPlan.AddRange(Enumerable.Repeat(ResponseStatuses.Sent, 620));
        statusPlan.AddRange(Enumerable.Repeat(ResponseStatuses.Duplicate, 316));
        statusPlan.AddRange(Enumerable.Repeat(ResponseStatuses.Error, 82));
        statusPlan.AddRange(Enumerable.Repeat(ResponseStatuses.InProgress, 230));
        for (var i = statusPlan.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (statusPlan[i], statusPlan[j]) = (statusPlan[j], statusPlan[i]);
        }

        for (var i = 0; i < total; i++)
        {
            var workerIndex = i % PreviewWorkerIds.Length;
            var workerId = PreviewWorkerIds[workerIndex];
            var accountNum = (i % 120) + 1;
            var accountId = Guid.Parse($"33333333-3333-3333-3333-{accountNum:D12}");
            var status = statusPlan[i];
            var adId = rng.Next(10_000_000, 99_999_999).ToString();
            var phoneDigits = $"79{rng.Next(10, 99)}{rng.Next(1000000, 9999999)}";
            var hidePhone = i % 17 == 0;
            var phoneChanged = !hidePhone && (i == 1 || i % 13 == 1);
            var phoneUnchanged = !hidePhone && !phoneChanged && (i == 2 || i % 11 == 2);
            var previousPhoneDigits = phoneChanged
                ? $"{phoneDigits[..^2]}{(int.Parse(phoneDigits[^2..], CultureInfo.InvariantCulture) + 1) % 100:D2}"
                : null;
            var createdAt = Now.AddMinutes(-(i * 4 + rng.Next(0, 20)));

            var bitrixEntityId = status == ResponseStatuses.Sent ? rng.Next(1000, 99999).ToString() : null;
            var age = i % 11 == 0 ? (int?)null : rng.Next(19, 56);
            var nameIndex = i % names.Length;
            rows.Add(new ResponseRowViewModel
            {
                Id = Guid.Parse($"55555555-5555-5555-5555-{(i + 1):D12}"),
                PersonId = Guid.Parse($"77777777-7777-7777-7777-{(nameIndex + 1):D12}"),
                CollectedAtUtc = createdAt,
                CreatedAtUtc = createdAt.AddMinutes(-rng.Next(5, 180)),
                FullName = names[nameIndex],
                Age = age,
                PhoneRaw = hidePhone ? string.Empty : $"+{phoneDigits}",
                PhoneNormalized = hidePhone ? string.Empty : phoneDigits,
                PhoneMetricKind = phoneChanged
                    ? ResponsePhoneMetricKinds.PhoneChanged
                    : phoneUnchanged
                        ? ResponsePhoneMetricKinds.PhoneUnchanged
                        : null,
                PhoneMetricLabel = phoneChanged
                    ? ResponsePhoneMetricKinds.FormatLabel(
                        ResponsePhoneMetricKinds.PhoneChanged,
                        previousPhone: ResponseDisplay.FormatPhone($"+{previousPhoneDigits}", previousPhoneDigits))
                    : phoneUnchanged
                        ? ResponsePhoneMetricKinds.FormatLabel(ResponsePhoneMetricKinds.PhoneUnchanged, unchangedHours: 48)
                        : null,
                PreviousPhoneRaw = previousPhoneDigits is null ? null : $"+{previousPhoneDigits}",
                PreviousPhoneNormalized = previousPhoneDigits,
                Vacancy = vacancies[i % vacancies.Length],
                VacancyUrl = $"https://www.avito.ru/item/{adId}",
                MessengerUrl = hidePhone ? string.Empty : $"https://www.avito.ru/profile/messenger/channel/{adId}",
                SourceResponseId = adId,
                City = cities[i % cities.Length],
                AccountId = accountId,
                AccountName = $"user_{accountNum:D2}",
                AvitoSubProfileName = accountNum % 3 == 0
                    ? null
                    : (accountNum % 2 == 0 ? "контракт РФ 7" : "Служба 3"),
                WorkerId = workerId,
                WorkerName = $"Worker #{workerIndex + 1}",
                Source = "Avito",
                Status = status,
                StatusLabel = status switch
                {
                    ResponseStatuses.Duplicate => "Дубль",
                    ResponseStatuses.Sent => "Отправлен · B24-1",
                    ResponseStatuses.ActionRequired => "Ожидает CRM",
                    ResponseStatuses.Error => "Ошибка Bitrix",
                    _ => "Уникальный"
                },
                BitrixLabel = status switch
                {
                    ResponseStatuses.Sent => "B24-1",
                    ResponseStatuses.Duplicate when i % 5 == 0 => "B24-1",
                    ResponseStatuses.Error => "B24-1",
                    _ => null
                },
                CanSend = status is ResponseStatuses.Error
                    or ResponseStatuses.ActionRequired
                    or ResponseStatuses.InProgress,
                StatusTone = status switch
                {
                    ResponseStatuses.Duplicate => "duplicate",
                    ResponseStatuses.Sent => "sent",
                    ResponseStatuses.ActionRequired => "action-required",
                    ResponseStatuses.Error => "error",
                    _ => "unique"
                },
                IsPhoneHidden = hidePhone,
                HasMessenger = !hidePhone,
                BitrixEntityId = bitrixEntityId,
                BitrixEntityUrl = bitrixEntityId is null
                    ? null
                    : $"https://demo.bitrix24.ru/crm/deal/details/{bitrixEntityId}/",
                CanResend = status is ResponseStatuses.Error
                    or ResponseStatuses.ActionRequired
                    or ResponseStatuses.InProgress
            });
        }

        return rows;
    }

    private static List<ResponseRowViewModel> FilterPreviewResponseRows(
        IReadOnlyList<ResponseRowViewModel> rows,
        ResponsesFilterViewModel filters,
        DashboardPeriod period)
    {
        IEnumerable<ResponseRowViewModel> query = rows;
        _ = period;

        if (!string.IsNullOrWhiteSpace(filters.SearchQuery))
        {
            query = query.Where(r =>
                SearchQueryNormalizer.MatchesTokens(
                    filters.SearchQuery,
                    r.FullName,
                    r.PhoneRaw,
                    r.PhoneNormalized,
                    r.Vacancy,
                    r.City,
                    r.AccountName));
        }

        if (!string.IsNullOrWhiteSpace(filters.VacancyQuery))
        {
            query = query.Where(r =>
                SearchQueryNormalizer.MatchesTokens(
                    filters.VacancyQuery,
                    r.Vacancy,
                    r.SourceResponseId));
        }

        if (filters.WorkerId is Guid workerId)
        {
            query = query.Where(r => r.WorkerId == workerId);
        }

        if (filters.AccountId is Guid accountId)
        {
            query = query.Where(r => r.AccountId == accountId);
        }

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            query = filters.Status switch
            {
                "unique" => query.Where(r => r.Status != ResponseStatuses.Duplicate),
                "duplicate" => query.Where(r => r.Status == ResponseStatuses.Duplicate),
                "sent" => query.Where(r => r.Status == ResponseStatuses.Sent),
                "action_required" => query.Where(r => r.Status == ResponseStatuses.ActionRequired),
                "error" => query.Where(r => r.Status == ResponseStatuses.Error),
                _ => query
            };
        }

        return query.ToList();
    }

    private static ResponseDetailViewModel? BuildPreviewResponseDetail(
        IReadOnlyList<ResponseRowViewModel> allRows,
        Guid selectedId)
    {
        var row = allRows.FirstOrDefault(r => r.Id == selectedId);
        if (row is null)
        {
            return null;
        }

        var rowIndex = ParsePreviewResponseIndex(row.Id);
        var (firstName, lastName, middleName) = SplitPreviewFullName(row.FullName);

        return new ResponseDetailViewModel
        {
            Id = row.Id,
            FullName = row.FullName,
            FirstName = firstName,
            LastName = lastName,
            MiddleName = middleName,
            Age = rowIndex % 4 == 0 ? 28 : rowIndex % 3 == 1 ? 34 : null,
            PhoneRaw = row.PhoneRaw,
            PhoneNormalized = row.PhoneNormalized,
            PhoneMetricKind = row.PhoneMetricKind,
            PhoneMetricLabel = row.PhoneMetricLabel,
            PreviousPhoneRaw = row.PreviousPhoneRaw,
            PreviousPhoneNormalized = row.PreviousPhoneNormalized,
            City = row.City,
            Vacancy = row.Vacancy,
            VacancyUrl = row.VacancyUrl,
            MessengerUrl = row.MessengerUrl,
            AccountId = row.AccountId,
            AccountName = row.AccountName,
            WorkerId = row.WorkerId,
            WorkerName = row.WorkerName,
            Source = row.Source,
            SourceResponseId = row.SourceResponseId,
            Status = row.Status,
            StatusLabel = row.StatusLabel,
            StatusTone = row.StatusTone,
            DuplicateSummary = row.Status == ResponseStatuses.Duplicate
                ? "Такой телефон уже был 12.03.2026 в 14:22"
                : null,
            BitrixEntityId = row.BitrixEntityId,
            BitrixEntityUrl = row.BitrixEntityUrl,
            ErrorMessage = row.Status == ResponseStatuses.Error
                ? "Bitrix24: поле «Телефон» не прошло валидацию"
                : null,
            RawText = row.IsPhoneHidden
                ? "Соискатель скрыл номер — узнать в чате"
                : $"{row.FullName} · {row.City} · отклик на «{row.Vacancy}»",
            ChatMessages = BuildPreviewChatMessages(rowIndex, row.CreatedAtUtc),
            PhoneHistory = BuildPreviewResponsePhoneHistory(row),
            CollectedAtUtc = row.CollectedAtUtc,
            CreatedAtUtc = row.CreatedAtUtc,
            ProcessedAtUtc = row.Status == ResponseStatuses.Sent
                ? row.CollectedAtUtc.AddMinutes(3)
                : null,
            CanResend = row.CanResend
        };
    }

    private static IReadOnlyList<CandidatePhoneHistoryDto> BuildPreviewResponsePhoneHistory(ResponseRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.PreviousPhoneNormalized))
        {
            return [];
        }

        return
        [
            new CandidatePhoneHistoryDto(
                row.PreviousPhoneRaw ?? row.PreviousPhoneNormalized,
                row.PreviousPhoneNormalized,
                row.CreatedAtUtc.AddDays(-2)),
            new CandidatePhoneHistoryDto(
                row.PhoneRaw,
                row.PhoneNormalized,
                row.CollectedAtUtc)
        ];
    }

    private static int ParsePreviewResponseIndex(Guid id)
    {
        var suffix = id.ToString("N")[^12..];
        return int.TryParse(suffix, out var index) ? Math.Max(0, index - 1) : 0;
    }

    private static (string First, string Last, string Middle) SplitPreviewFullName(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => ("", "", ""),
            1 => (parts[0], "", ""),
            2 => (parts[0], parts[1], ""),
            _ => (parts[0], parts[1], string.Join(' ', parts.Skip(2)))
        };
    }

    private static IReadOnlyList<ResponseChatMessageViewModel> BuildPreviewChatMessages(int rowIndex, DateTime createdAtUtc)
    {
        if (rowIndex == 0)
        {
            return
            [
                new()
                {
                    Text = "Кандидат откликнулся на вакансию «Кровать двуспальная 180×200»",
                    TimeLabel = FormatPreviewChatTime(createdAtUtc, 42),
                    Tone = "system"
                },
                new()
                {
                    Text = "Здравствуйте! Интересует вакансия, ещё актуально?",
                    TimeLabel = FormatPreviewChatTime(createdAtUtc, 38),
                    Tone = "incoming"
                },
                new()
                {
                    Text = "Добрый день! Да, вакансия открыта. Когда удобно созвониться?",
                    TimeLabel = FormatPreviewChatTime(createdAtUtc, 31),
                    Tone = "outgoing"
                },
                new()
                {
                    Text = "Могу сегодня после 18:00 или завтра с 10:00",
                    TimeLabel = FormatPreviewChatTime(createdAtUtc, 24),
                    Tone = "incoming"
                },
                new()
                {
                    Text = "Отлично, жду звонка сегодня вечером. Если не дозвонитесь — напишите сюда.",
                    TimeLabel = FormatPreviewChatTime(createdAtUtc, 18),
                    Tone = "outgoing"
                }
            ];
        }

        if (rowIndex % 7 == 3)
        {
            return
            [
                new()
                {
                    Text = "Добрый день! Можно уточнить график работы?",
                    TimeLabel = FormatPreviewChatTime(createdAtUtc, 15),
                    Tone = "incoming"
                },
                new()
                {
                    Text = "Здравствуйте! График 2/2, с 9:00 до 21:00. Подробности в объявлении.",
                    TimeLabel = FormatPreviewChatTime(createdAtUtc, 9),
                    Tone = "outgoing"
                }
            ];
        }

        return [];
    }

    private static string FormatPreviewChatTime(DateTime createdAtUtc, int minutesBefore)
    {
        var local = createdAtUtc.AddMinutes(-minutesBefore).ToLocalTime();
        return local.ToString("dd MMM HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
    }
}
