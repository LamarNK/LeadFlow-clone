namespace LeadFlow.Core.Services.Avito;

/// <summary>Phone-watch cards are contractual work and must not compete with the random browsing budget.</summary>
internal static class CandidatePhoneRevealBudget
{
    public static int Resolve(int regularBudget, int openPhoneWatchCount) =>
        Math.Max(Math.Max(0, regularBudget), Math.Max(0, openPhoneWatchCount));

    /// <summary>
    /// Адаптивный бюджет: когда на странице остаётся хвост замаскированных карточек,
    /// базовый бюджет не успевает их разгребать между проходами (новые отклики
    /// конкурируют со старыми за клики). Растим бюджет пропорционально хвосту,
    /// но не выше жёсткого потолка. Потолок ограничивает только прирост от хвоста:
    /// бюджет, поднятый числом открытых phone-watch, не урезаем (контрактная работа).
    /// </summary>
    public static int ResolveAdaptive(int currentBudget, int maskedPending)
    {
        var current = Math.Max(0, currentBudget);
        var masked = Math.Max(0, maskedPending);
        if (masked <= current + MonitoringTiming.PhoneRevealBacklogMinSurplus)
        {
            return current;
        }

        var surplus = masked - current;
        var extra = (int)Math.Ceiling(surplus / (double)MonitoringTiming.PhoneRevealBacklogExtraDivisor);
        return Math.Max(
            current,
            Math.Min(
                current + extra,
                MonitoringTiming.PhoneRevealBudgetHardCapPerCycle));
    }
}
