namespace LeadFlow.Core.Services;

/// <summary>Параметры мониторинга откликов; не выставляются в UI, задаются в коде.</summary>
public static class MonitoringTiming
{
    /// <summary>Интервал опроса конфига, пока нет активных аккаунтов (без циклов мониторинга).</summary>
    public const int NoAccountsConfigPollSeconds = 60;

    /// <summary>Пауза после изменения списка аккаунтов в панели перед стартом мониторинга.</summary>
    public const int MonitoringStartDebounceSeconds = 60;

    /// <summary>Повторная попытка установки скачанного обновления в безопасном окне.</summary>
    public const int PendingUpdateRetrySeconds = 15;

    /// <summary>Минимальная пауза между циклами при высокой доле новых откликов в последнем цикле.</summary>
    public const int CycleDelayMinMinutes = 3;

    /// <summary>Максимальная пауза между циклами, если в последнем цикле новых откликов не было.</summary>
    public const int CycleDelayMaxMinutes = 20;

    /// <summary>К пустому циклу N… добавляется (N−1)×шаг минут (см. Max), чтобы реже дергать Авито при долгой тишине.</summary>
    public const int CycleQuietBackoffExtraMinutesPerStep = 4;

    /// <summary>Верхняя граница добавки к паузе при длинной серии пустых циклов.</summary>
    public const int CycleQuietBackoffExtraMinutesMax = 75;

    /// <summary>Сколько дней истории учитывать для «календаря» прихода откликов (локальное время ПК).</summary>
    public const int CycleHistoricalHeatLookbackDays = 56;

    /// <summary>Минимум записей в истории, чтобы оценка «жары» слота была значимой.</summary>
    public const int CycleHistoricalHeatMinSamples = 24;

    /// <summary>Насколько сильно исторический слот (день недели + час) может поднять «активность» при расчёте паузы (0…1).</summary>
    public const double CycleHistoricalHeatActivityBoostCap = 0.55;

    public const int DelayBetweenAccountsSeconds = 15;

    /// <summary>Сдвиг старта параллельных аккаунтов (мс), чтобы не бить в AdsPower Local API пачкой browser/start.</summary>
    public const int ParallelAccountLaunchStaggerMs = 2000;

    /// <summary>
    /// После CDP-подключения ждём, пока AdsPower сам применит open_urls.
    /// Новую вкладку не создаём — навигируем уже открытую через Page.navigate.
    /// </summary>
    public const int AdsPowerStartupNavigationMaxWaitMs = 5_000;

    /// <summary>Интервал опроса URL стартовой вкладки AdsPower (мс).</summary>
    public const int AdsPowerStartupNavigationPollMs = 250;

    /// <summary>Сколько ждать смену URL после Page.navigate / location.assign (мс).</summary>
    public const int AdsPowerForcedNavigationMaxWaitMs = 8_000;

    /// <summary>
    /// После стольких полных циклов мониторинга (с browser/start…browser/stop)
    /// воркер принудительно закрывает все известные браузеры AdsPower —
    /// чтобы окна, открытые оператором «посмотреть», не висели бесконечно.
    /// Также уборка срабатывает при смене локального календарного дня и при остановке мониторинга.
    /// </summary>
    public const int BrowserHousekeepingEveryNCycles = 3;
    public const int DelayBetweenResponsesSeconds = 8;

    /// <summary>
    /// Оценка «ёмкости» цикла для расчёта паузы между проходами (<see cref="MonitoringCycleDelay"/>).
    /// Жёсткого лимита публикаций нет — скорость ограничивают <see cref="HumanDelayBetweenResponsesMinSeconds"/> и клики на Avito.
    /// </summary>
    public const int TypicalResponsesPerAccountPerCycle = 30;

    [Obsolete("Публикации больше не ограничиваются. Используйте TypicalResponsesPerAccountPerCycle для эвристик паузы.")]
    public const int MaxResponsesPerAccountPerCycle = TypicalResponsesPerAccountPerCycle;

    [Obsolete("Публикации больше не ограничиваются. Используйте TypicalResponsesPerAccountPerCycle для эвристик паузы.")]
    public const int MaxResponsesPerSubProfilePerCycle = TypicalResponsesPerAccountPerCycle;
    public const int ActiveAdsRefreshIntervalMinutes = 75;

    /// <summary>Собирать статистику объявлений в проходе Orbita.Worker (отклики + объявления). Пока выключено.</summary>
    public const bool CollectActiveAdsInWorkerPass = false;

    /// <summary>Как часто воркер перечитывает модалку «Выбор профиля» Avito Pro (часы).</summary>
    public const int SubProfilesRefreshIntervalHours = 24;

    // ---- «Человеческие» рандомные паузы ----
    // Идея: после открытия страницы / переключения профиля / обработки отклика
    // имитируем чтение пользователем, чтобы не палить ботскую частоту запросов.

    /// <summary>Между обработкой откликов: рандом в [min..max] секунд (вокруг <see cref="DelayBetweenResponsesSeconds"/>).</summary>
    public const int HumanDelayBetweenResponsesMinSeconds = 5;
    public const int HumanDelayBetweenResponsesMaxSeconds = 14;

    /// <summary>После переключения суб-профиля и до того, как тянуть с него данные.</summary>
    public const int HumanDelayAfterProfileSwitchMinMs = 6000;
    public const int HumanDelayAfterProfileSwitchMaxMs = 15000;

    /// <summary>После загрузки страницы /profile/pro/items до снятия HTML — даём «дочитать» SPA + лёгкий jitter.</summary>
    public const int HumanDelayAfterItemsRenderMinMs = 5000;
    public const int HumanDelayAfterItemsRenderMaxMs = 10000;

    /// <summary>После загрузки модалки переключения профилей.</summary>
    public const int HumanDelayAfterSwitchModalMinMs = 800;
    public const int HumanDelayAfterSwitchModalMaxMs = 2200;

    /// <summary>Пауза между суб-профилями на одном аккаунте — крупнее, имитируем «походили по кабинету».</summary>
    public const int HumanDelayBetweenSubProfilesMinMs = 8000;
    public const int HumanDelayBetweenSubProfilesMaxMs = 18000;

    /// <summary>Перед кликом по карточке отклика (панель «Данные», чат) — короткий jitter, без долгих пауз.</summary>
    public const int HumanDelayBeforeCandidateClickMinMs = 450;
    public const int HumanDelayBeforeCandidateClickMaxMs = 950;

    /// <summary>После клика по карточке до чтения панели или мини-чата.</summary>
    public const int HumanDelayAfterCandidateClickMinMs = 520;
    public const int HumanDelayAfterCandidateClickMaxMs = 1100;

    // ---- Ожидание готовности страницы откликов (сигналы DOM, не только таймер) ----

    /// <summary>Максимум ожидания списка откликов после перехода / reload (мс).</summary>
    public const int CandidatesPageMaxWaitMs = 60_000;

    /// <summary>Интервал опроса DOM при ожидании откликов (мс).</summary>
    public const int CandidatesPagePollMs = 500;

    /// <summary>Сколько подряд одинаковых снимков списка нужно, чтобы считать страницу стабильной.</summary>
    public const int CandidatesPageStablePollsRequired = 2;

    /// <summary>
    /// После reload: если сигнатура совпадает с baseline, но DOM стабилен столько мс — принимаем
    /// (пустой список или Avito не обновил карточки, но страница готова).
    /// </summary>
    public const int CandidatesBaselineStaleAcceptGraceMs = 12_000;

    /// <summary>Сколько раз повторять переключение субпрофиля с восстановлением страницы между попытками.</summary>
    public const int SubProfileSwitchMaxAttempts = 3;

    /// <summary>Максимум ожидания подтверждения активного суб-профиля после switch (мс).</summary>
    public const int VerifySubProfileMaxWaitMs = 20_000;

    /// <summary>Интервал опроса активного суб-профиля (мс).</summary>
    public const int VerifySubProfilePollMs = 650;

    /// <summary>
    /// Через сколько часов сбрасывать блокирующий статус аккаунта (капча, вход) и повторять проход.
    /// Пока срок не истёк, воркер не открывает браузер, чтобы не долбить Avito.
    /// </summary>
    public const int AccountBlockingIssueRetryAfterHours = 6;

    /// <summary>
    /// Через сколько дней сбрасывать устаревшее сообщение об ошибке на аккаунте без блокирующего статуса.
    /// </summary>
    public const int AccountStaleErrorMessageMaxAgeDays = 3;

    /// <summary>Пауза после обновления вкладки перед повторной проверкой сессии (мс).</summary>
    public const int AutoLoginPreRefreshSettleMs = 900;

    /// <summary>Пауза после перехода на dashboard перед повторной проверкой сессии (мс).</summary>
    public const int AutoLoginDashboardNavSettleMs = 700;

    /// <summary>Пауза после клика «Вход» до появления списка профилей или формы пароля (мс).</summary>
    public const int AutoLoginAfterOpenLoginMs = 1400;

    /// <summary>Пауза после выбора сохранённого профиля (мс).</summary>
    public const int AutoLoginAfterUserSelectMs = 1200;

    /// <summary>Сколько раз опрашивать автозаполнение пароля.</summary>
    public const int AutoLoginPasswordAutofillPolls = 6;

    /// <summary>Интервал опроса автозаполнения пароля (мс).</summary>
    public const int AutoLoginPasswordAutofillPollMs = 500;

    /// <summary>Максимум ожидания успешного входа после submit (мс).</summary>
    public const int AutoLoginPostSubmitMaxWaitMs = 15_000;

    /// <summary>Интервал опроса после submit (мс).</summary>
    public const int AutoLoginPostSubmitPollMs = 650;

    /// <summary>Пауза после ввода текста в мини-чат перед отправкой (мс).</summary>
    public const int MessengerAutoReplyAfterTypeMinMs = 350;

    /// <summary>Пауза после ввода текста в мини-чат перед отправкой (мс).</summary>
    public const int MessengerAutoReplyAfterTypeMaxMs = 900;

    /// <summary>Ожидание появления отправленного сообщения в истории чата (мс).</summary>
    public const int MessengerAutoReplyPostSendMaxWaitMs = 8_000;

    /// <summary>Интервал опроса истории после отправки (мс).</summary>
    public const int MessengerAutoReplyPostSendPollMs = 450;
}
