namespace LeadFlow.Core.Services;

/// <summary>Параметры мониторинга откликов; не выставляются в UI, задаются в коде.</summary>
public static class MonitoringTiming
{
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
    public const int DelayBetweenResponsesSeconds = 8;
    public const int MaxResponsesPerAccountPerCycle = 10;
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

    /// <summary>Максимум ожидания подтверждения активного суб-профиля после switch (мс).</summary>
    public const int VerifySubProfileMaxWaitMs = 20_000;

    /// <summary>Интервал опроса активного суб-профиля (мс).</summary>
    public const int VerifySubProfilePollMs = 650;
}
