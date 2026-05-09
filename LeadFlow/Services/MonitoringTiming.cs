namespace LeadFlow.Services;

/// <summary>Параметры мониторинга откликов; не выставляются в UI, задаются в коде.</summary>
public static class MonitoringTiming
{
    /// <summary>Минимальная пауза между циклами при высокой доле новых откликов в последнем цикле.</summary>
    public const int CycleDelayMinMinutes = 1;

    /// <summary>Максимальная пауза между циклами, если в последнем цикле новых откликов не было.</summary>
    public const int CycleDelayMaxMinutes = 10;

    /// <summary>К пустому циклу N… добавляется (N−1)×шаг минут (см. Max), чтобы реже дергать Авито при долгой тишине.</summary>
    public const int CycleQuietBackoffExtraMinutesPerStep = 2;

    /// <summary>Верхняя граница добавки к паузе при длинной серии пустых циклов.</summary>
    public const int CycleQuietBackoffExtraMinutesMax = 50;

    /// <summary>Сколько дней истории учитывать для «календаря» прихода откликов (локальное время ПК).</summary>
    public const int CycleHistoricalHeatLookbackDays = 56;

    /// <summary>Минимум записей в истории, чтобы оценка «жары» слота была значимой.</summary>
    public const int CycleHistoricalHeatMinSamples = 24;

    /// <summary>Насколько сильно исторический слот (день недели + час) может поднять «активность» при расчёте паузы (0…1).</summary>
    public const double CycleHistoricalHeatActivityBoostCap = 0.55;

    public const int DelayBetweenAccountsSeconds = 10;
    public const int DelayBetweenResponsesSeconds = 3;
    public const int MaxResponsesPerAccountPerCycle = 10;
    public const int ActiveAdsRefreshIntervalMinutes = 45;

    // ---- «Человеческие» рандомные паузы ----
    // Идея: после открытия страницы / переключения профиля / обработки отклика
    // имитируем чтение пользователем, чтобы не палить ботскую частоту запросов.

    /// <summary>Между обработкой откликов: рандом в [min..max] секунд (вокруг <see cref="DelayBetweenResponsesSeconds"/>).</summary>
    public const int HumanDelayBetweenResponsesMinSeconds = 2;
    public const int HumanDelayBetweenResponsesMaxSeconds = 6;

    /// <summary>После переключения суб-профиля и до того, как тянуть с него данные.</summary>
    public const int HumanDelayAfterProfileSwitchMinMs = 1500;
    public const int HumanDelayAfterProfileSwitchMaxMs = 4500;

    /// <summary>После загрузки страницы /profile/pro/items до снятия HTML — даём «дочитать» SPA + лёгкий jitter.</summary>
    public const int HumanDelayAfterItemsRenderMinMs = 1500;
    public const int HumanDelayAfterItemsRenderMaxMs = 3500;

    /// <summary>После загрузки модалки переключения профилей.</summary>
    public const int HumanDelayAfterSwitchModalMinMs = 400;
    public const int HumanDelayAfterSwitchModalMaxMs = 1200;

    /// <summary>Пауза между суб-профилями на одном аккаунте — крупнее, имитируем «походили по кабинету».</summary>
    public const int HumanDelayBetweenSubProfilesMinMs = 2500;
    public const int HumanDelayBetweenSubProfilesMaxMs = 7000;
}
