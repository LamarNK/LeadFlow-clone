using LeadFlow.Models;

namespace LeadFlow.ViewModels;

public enum AdsDashboardFilter
{
    All,
    Active,
    Blocked
}

public enum AdsSortOption
{
    ByViews,
    ByContacts,
    ByStatus,
    ByDeleteDate
}

public enum DashboardAdKind
{
    Active,
    Blocked
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

/// <summary>
/// <summary>Элемент единой сетки объявлений на главном экране (активные / заблокированные).</summary>
/// </summary>
public sealed class DashboardAdDisplayItem
{
    public DashboardAdDisplayItem(DashboardAdKind kind, AvitoAdStatus ad)
    {
        Kind = kind;
        Ad = ad;
    }

    public DashboardAdKind Kind { get; }
    public AvitoAdStatus Ad { get; }
}
