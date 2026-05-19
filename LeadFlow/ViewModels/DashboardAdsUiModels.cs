using LeadFlow.Models;

namespace LeadFlow.ViewModels;

public enum AdsDashboardFilter
{
    All,
    Active,
    Blocked,
    WithMessages,
    WithoutMessages,
    Drafts,
    WithIssues
}

public enum AdsSortOption
{
    ByViews,
    ByContacts,
    ByStatus,
    ByDeleteDate,
    ByViewsAscending,
    ByContactsAscending,
    ByNewestFirst,
    ByProblemsFirst
}

public enum DashboardAdKind
{
    Active,
    Blocked
}

public enum DashboardAdBadgeKind
{
    Active,
    Blocked,
    Draft,
    Messages,
    NeedsAction
}

public sealed class AdsFilterTab(AdsDashboardFilter filter, string title)
{
    public AdsDashboardFilter Filter { get; } = filter;
    public string Title { get; } = title;
}

public sealed class AdsSortChoice(AdsSortOption option, string label)
{
    public AdsSortOption Option { get; } = option;
    public string Label { get; } = label;

    public override string ToString() => Label;
}

/// <summary>Элемент единой сетки объявлений на главном экране (активные / заблокированные).</summary>
public sealed class DashboardAdDisplayItem
{
    public DashboardAdDisplayItem(DashboardAdKind kind, AvitoAdStatus ad)
    {
        Kind = kind;
        Ad = ad;
    }

    public DashboardAdKind Kind { get; }
    public AvitoAdStatus Ad { get; }

    public bool IsDraftAd =>
        Ad.Status.Contains("черновик", StringComparison.CurrentCultureIgnoreCase)
        || Ad.Status.Contains("draft", StringComparison.CurrentCultureIgnoreCase);

    public bool IsBlockedPresentation => Kind == DashboardAdKind.Blocked;

    public bool NeedsActionStatusBadge =>
        !IsBlockedPresentation
        && !IsDraftAd
        && (Ad.Status.Contains("действ", StringComparison.CurrentCultureIgnoreCase)
            || Ad.Status.Contains("модерац", StringComparison.CurrentCultureIgnoreCase)
            || Ad.Status.Contains("истёк", StringComparison.CurrentCultureIgnoreCase)
            || Ad.Status.Contains("истек", StringComparison.CurrentCultureIgnoreCase)
            || Ad.Status.Contains("требу", StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Один главный бейдж по приоритету для карточки.</summary>
    public DashboardAdBadgeKind PrimaryBadgeKind
    {
        get
        {
            if (Kind == DashboardAdKind.Blocked)
            {
                return DashboardAdBadgeKind.Blocked;
            }

            if (IsDraftAd)
            {
                return DashboardAdBadgeKind.Draft;
            }

            if (NeedsActionStatusBadge)
            {
                return DashboardAdBadgeKind.NeedsAction;
            }

            if (Ad.Contacts > 0)
            {
                return DashboardAdBadgeKind.Messages;
            }

            return DashboardAdBadgeKind.Active;
        }
    }

    public bool HasUnreadMessages => Ad.Contacts > 0;

    /// <summary>Компактная подпись для синего бейджа сообщений.</summary>
    public string MessageCountPillText
    {
        get
        {
            var n = Ad.Contacts;
            if (n <= 0)
            {
                return string.Empty;
            }

            return n switch
            {
                1 => "1 сообщение",
                2 or 3 or 4 => $"{n} сообщения",
                _ => $"{n} сообщений"
            };
        }
    }

    /// <summary>Показывать отдельный синий бейдж с числом, если есть сообщения, но главный бейдж — другой.</summary>
    public bool ShowMessageCountPill =>
        Ad.Contacts > 0 && PrimaryBadgeKind != DashboardAdBadgeKind.Messages;

    /// <summary>Скрыть строку «Активно» без особых статусов.</summary>
    public bool ShowPrimaryStatusBadge => PrimaryBadgeKind != DashboardAdBadgeKind.Active;

    public bool ShowAnyBadgeRow => ShowPrimaryStatusBadge || ShowMessageCountPill;

    /// <summary>Ключи для <see cref="System.ComponentModel.ICollectionView"/> (сортировка без пересборки коллекции).</summary>
    public int Views => Ad.Views;

    public int Contacts => Ad.Contacts;

    public string TitleSort => Ad.Title;

    public string StatusSort => Ad.Status;

    public string DeleteDateSortKey => string.IsNullOrEmpty(Ad.DeleteDate) ? "\uFFFF" : Ad.DeleteDate;

    public int DaysOnAvitoSort => Ad.DaysOnAvito;

    public int ProblemAttentionRankSort
    {
        get
        {
            if (Kind == DashboardAdKind.Blocked)
            {
                return 400;
            }

            if (IsDraftAd)
            {
                return 300;
            }

            if (Ad.Status.Contains("отклон", StringComparison.OrdinalIgnoreCase)
                || Ad.Status.Contains("модерац", StringComparison.OrdinalIgnoreCase)
                || Ad.Status.Contains("наруш", StringComparison.OrdinalIgnoreCase)
                || Ad.Status.Contains("действ", StringComparison.OrdinalIgnoreCase)
                || Ad.Status.Contains("требу", StringComparison.OrdinalIgnoreCase)
                || Ad.Status.Contains("истёк", StringComparison.OrdinalIgnoreCase)
                || Ad.Status.Contains("истек", StringComparison.OrdinalIgnoreCase))
            {
                return 200;
            }

            return Ad.Contacts > 0 ? 50 : 0;
        }
    }

    public string StatusBadgeCaption => PrimaryBadgeKind switch
    {
        DashboardAdBadgeKind.Blocked => "Заблокировано",
        DashboardAdBadgeKind.Draft => "Черновик",
        DashboardAdBadgeKind.Messages => MessageCountPillText,
        DashboardAdBadgeKind.NeedsAction => "Нужны действия",
        _ => "Активно"
    };
}
