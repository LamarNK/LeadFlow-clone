namespace LeadFlow.Models;

public static class BalanceDisplayRules
{
    /// <summary>Порог «низкого» авансового баланса Avito (₽) для подсветки в UI.</summary>
    public const decimal LowBalanceThresholdRub = 5000m;

    /// <summary>Сколько аккаунтов показывать в превью на главном экране.</summary>
    public const int DashboardPreviewAccountLimit = 6;

    /// <summary>Субпрофилей больше этого — в сводке сворачиваем аккаунт по умолчанию.</summary>
    public const int CollapseSubProfilesAbove = 3;
}