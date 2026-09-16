

using CommunityToolkit.Mvvm.ComponentModel;

namespace LeadFlow.ViewModels;

public enum AdsScopeKind { All, Account, SubProfile }

public sealed class AdsScopeItem
{
    public AdsScopeItem(AdsScopeKind kind, string title, Guid? accountId = null, string? subProfileId = null, int indent = 0)
    { Kind = kind; Title = title; AccountId = accountId; SubProfileId = subProfileId; Indent = indent; }
    public AdsScopeKind Kind { get; }
    public string Title { get; }
    public Guid? AccountId { get; }
    public string? SubProfileId { get; }
    public int Indent { get; }
    public string DisplayTitle => Indent > 0 ? $"   └ {Title}" : Title;
}

public enum AdsDashboardFilter
{
    All,
    Active,
    Blocked,
    Unpublished,
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
    Blocked,
    Unpublished
}

public enum DashboardAdBadgeKind
{
    Active,
    Blocked,
    Unpublished,
    Draft,
    Messages,
    NeedsAction
}

public enum AdRenewalUiState
{
    Ready,
    Running,
    Succeeded,
    Failed
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

/// <summary>Элемент единой сетки объявлений на главном экране (активные / заблокированные / неопубликованные).</summary>
public sealed class DashboardAdDisplayItem : ObservableObject
{
    private AdRenewalUiState renewalState;
    private string renewalMessage = string.Empty;

    public DashboardAdDisplayItem(
        DashboardAdKind kind,
        AvitoAdStatus ad,
        bool supportsRenewalAutomation = true)
    {
        Kind = kind;
        Ad = ad;
        SupportsRenewalAutomation = supportsRenewalAutomation;
    }

    public DashboardAdKind Kind { get; }
    public AvitoAdStatus Ad { get; }
    public bool SupportsRenewalAutomation { get; }

    public bool IsDraftAd =>
        Ad.Status.Contains("черновик", StringComparison.CurrentCultureIgnoreCase)
        || Ad.Status.Contains("draft", StringComparison.CurrentCultureIgnoreCase);

    public bool IsBlockedPresentation => Kind == DashboardAdKind.Blocked;
    public bool IsUnpublishedPresentation => Kind == DashboardAdKind.Unpublished;

    public AdRenewalUiState RenewalState
    {
        get => renewalState;
        private set
        {
            if (!SetProperty(ref renewalState, value)) return;
            OnPropertyChanged(nameof(IsRenewalRunning));
            OnPropertyChanged(nameof(IsRenewalSucceeded));
            OnPropertyChanged(nameof(IsRenewalFailed));
            OnPropertyChanged(nameof(CanStartRenewal));
            OnPropertyChanged(nameof(RenewalButtonCaption));
        }
    }

    public string RenewalMessage
    {
        get => renewalMessage;
        private set => SetProperty(ref renewalMessage, value);
    }

    public bool ShowRenewalAction => IsUnpublishedPresentation && Ad.CanPublish && SupportsRenewalAutomation;
    public bool ShowRenewalUnavailable => IsUnpublishedPresentation && !ShowRenewalAction;
    public bool IsRenewalRunning => RenewalState == AdRenewalUiState.Running;
    public bool IsRenewalSucceeded => RenewalState == AdRenewalUiState.Succeeded;
    public bool IsRenewalFailed => RenewalState == AdRenewalUiState.Failed;
    public bool CanStartRenewal => ShowRenewalAction && !IsRenewalRunning && !IsRenewalSucceeded;
    public string RenewalButtonCaption => RenewalState switch
    {
        AdRenewalUiState.Running => "Публикуем…",
        AdRenewalUiState.Failed => "Повторить публикацию",
        AdRenewalUiState.Succeeded => "Отправлено",
        _ => "Опубликовать на 30 дней"
    };

    public string RenewalUnavailableText =>
        !SupportsRenewalAutomation
            ? "Автопродление доступно для профилей AdsPower. Откройте объявление и опубликуйте его вручную."
            : Ad.Status.Contains("ожида", StringComparison.CurrentCultureIgnoreCase)
        || Ad.Status.Contains("провер", StringComparison.CurrentCultureIgnoreCase)
            ? "Публикация уже отправлена в Avito — ожидаем обновления статуса."
            : "Продление недоступно: Avito не показывает действие «Опубликовать».";

    public void SetRenewalRunning()
    {
        RenewalMessage = "Открываем аккаунт и проверяем объявление…";
        RenewalState = AdRenewalUiState.Running;
    }

    public void SetRenewalResult(AvitoAdRenewalResult result)
    {
        RenewalMessage = result.Message;
        RenewalState = result.Success ? AdRenewalUiState.Succeeded : AdRenewalUiState.Failed;
    }

    public bool NeedsActionStatusBadge =>
        !IsBlockedPresentation && !IsUnpublishedPresentation
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

            if (Kind == DashboardAdKind.Unpublished)
            {
                return DashboardAdBadgeKind.Unpublished;
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

            if (Kind == DashboardAdKind.Unpublished)
            {
                return 350;
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
        DashboardAdBadgeKind.Unpublished => "Не опубликовано",
        DashboardAdBadgeKind.Draft => "Черновик",
        DashboardAdBadgeKind.Messages => MessageCountPillText,
        DashboardAdBadgeKind.NeedsAction => "Нужны действия",
        _ => "Активно"
    };
}
