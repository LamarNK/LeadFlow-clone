using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services.Avito;
using System.Collections.ObjectModel;

namespace LeadFlow.ViewModels;

public partial class DashboardViewModel(AppRepository repository) : ObservableObject
{
    [ObservableProperty]
    private int newResponses;

    [ObservableProperty]
    private int totalToday;

    [ObservableProperty]
    private int sentToCrm;

    [ObservableProperty]
    private int inProgress;

    [ObservableProperty]
    private int duplicates;

    [ObservableProperty]
    private int errors;

    [ObservableProperty]
    private int connectedAccounts;

    [ObservableProperty]
    private int requiresAuthorization;

    // === Статистика объявлений Авито ===
    [ObservableProperty]
    private int blockedAdsCount;

    [ObservableProperty]
    private int draftsCount;

    [ObservableProperty]
    private int totalActiveViews;

    [ObservableProperty]
    private int totalActiveContacts;

    public ObservableCollection<ActivityPoint> Activity { get; } = new();
    public ObservableCollection<AvitoAdStatus> ActiveAds { get; } = new();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var stats = await repository.GetDashboardStatsAsync(CancellationToken.None);
        NewResponses = stats.NewResponses;
        TotalToday = stats.TotalToday;
        SentToCrm = stats.SentToCrm;
        InProgress = stats.InProgress;
        Duplicates = stats.Duplicates;
        Errors = stats.Errors;
        ConnectedAccounts = stats.ConnectedAccounts;
        RequiresAuthorization = stats.RequiresAuthorization;
        BlockedAdsCount = stats.BlockedAdsCount;
        DraftsCount = stats.DraftsCount;
        Activity.Clear();
        foreach (var point in stats.Activity)
        {
            Activity.Add(point);
        }
        ActiveAds.Clear();
        TotalActiveViews = 0;
        TotalActiveContacts = 0;
    }
}
