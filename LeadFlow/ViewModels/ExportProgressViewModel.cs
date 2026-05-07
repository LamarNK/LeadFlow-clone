using CommunityToolkit.Mvvm.ComponentModel;

namespace LeadFlow.ViewModels;

/// <summary>Данные для окна прогресса экспорта/импорта архива профиля (0–100 %).</summary>
public partial class ExportProgressViewModel(string accountDisplayName, string headline) : ObservableObject
{
    public ExportProgressViewModel(string accountDisplayName)
        : this(accountDisplayName, "Создаётся архив профиля…")
    {
    }

    public string AccountDisplayName { get; } = accountDisplayName;

    public string Headline { get; } = headline;

    [ObservableProperty]
    private int percent;

    public string DoneLine => $"Готово: {Percent} %";

    public string RemainingLine => $"Осталось: {Math.Max(0, 100 - Percent)} %";

    partial void OnPercentChanged(int value)
    {
        OnPropertyChanged(nameof(DoneLine));
        OnPropertyChanged(nameof(RemainingLine));
    }
}
