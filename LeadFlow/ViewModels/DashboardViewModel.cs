using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services;
using System.Collections.ObjectModel;
using System.Windows;

namespace LeadFlow.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly AppRepository _repository;
    private readonly IMonitoringService _monitoringService;

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

    public DashboardViewModel(AppRepository repository, IMonitoringService monitoringService)
    {
        _repository = repository;
        _monitoringService = monitoringService;
        _monitoringService.ProfileStatsUpdated += (_, _) => ApplyActiveAdsSnapshot();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var stats = await _repository.GetDashboardStatsAsync(CancellationToken.None);
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

        ApplyActiveAdsSnapshot();
    }

    private void ApplyActiveAdsSnapshot()
    {
        var snapshot = _monitoringService.GetActiveAdsSnapshot();

        void UpdateCollection()
        {
            ActiveAds.Clear();
            foreach (var ad in snapshot.OrderByDescending(static ad => ad.Views).ThenBy(static ad => ad.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                ActiveAds.Add(ad);
            }

            TotalActiveViews = snapshot.Sum(static ad => ad.Views);
            TotalActiveContacts = snapshot.Sum(static ad => ad.Contacts);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(UpdateCollection);
            return;
        }

        UpdateCollection();
    }
}
