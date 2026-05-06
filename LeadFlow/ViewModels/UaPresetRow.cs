using CommunityToolkit.Mvvm.ComponentModel;

namespace LeadFlow.ViewModels;

public sealed partial class UaPresetRow : ObservableObject
{
    public bool IsAllOption { get; init; }
    public int? UaMajor { get; init; }
    public string Label { get; init; } = string.Empty;

    [ObservableProperty] private bool isChecked;
}
