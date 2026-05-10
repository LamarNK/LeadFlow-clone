using System.Diagnostics.CodeAnalysis;

namespace LeadFlow.Services.Avito;

/// <summary>
/// Чистые правила: когда нельзя затирать непустой снимок вакансий пустым списком или сомнительными данными.
/// </summary>
public static class ActiveAdsSnapshotApplyPolicy
{
    /// <summary>
    /// Возвращает true, если применять снимок к БД/памяти нельзя (оставляем прежний список).
    /// </summary>
    public static bool ShouldSkipApplyingSnapshot(
        ProfileResult snapshot,
        int oldAdsCount,
        int newAdsCount,
        [NotNullWhen(true)] out string? reason)
    {
        reason = null;
        if (!snapshot.ParseSuccess)
        {
            return false;
        }

        if (oldAdsCount <= 0 || newAdsCount != 0)
        {
            return false;
        }

        // Вкладка «Активные» (все категории) показывает N&gt;0, а вакансий в /rabota|/vakansii не распарсилось — не затираем сохранённые вакансии.
        if (snapshot.ActiveCount > 0)
        {
            reason = "tab_active_count_positive_but_zero_vacancy_rows";
            return true;
        }

        if (snapshot.ActiveCount == 0)
        {
            if (!snapshot.PageLoadedSuccessfully)
            {
                reason = "page_load_not_confirmed";
                return true;
            }

            if (!snapshot.ActiveTabCounterResolved)
            {
                reason = "active_tab_counter_not_found_in_html";
                return true;
            }

            if (snapshot.ItemSnippetMarkersFound > 0)
            {
                reason = "item_snippet_markers_present_but_zero_vacancy_rows";
                return true;
            }
        }

        return false;
    }
}
