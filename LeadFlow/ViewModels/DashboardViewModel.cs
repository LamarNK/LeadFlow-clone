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
        ApplyStats(stats);

        ApplyActiveAdsSnapshot();
    }

    public void ApplyProcessedResponse(CandidateResponse response)
    {
        if (response.CreatedAt < DateTime.UtcNow.Date)
        {
            return;
        }

        NewResponses++;
        TotalToday++;

        switch (response.Status)
        {
            case ResponseStatus.Sent:
                SentToCrm++;
                break;
            case ResponseStatus.InProgress:
                InProgress++;
                break;
            case ResponseStatus.Duplicate:
                Duplicates++;
                break;
            case ResponseStatus.Error:
                Errors++;
                break;
        }

        EnsureActivityBuckets();
        var bucketIndex = Math.Clamp(response.CreatedAt.Hour / 3, 0, Activity.Count - 1);
        var bucket = Activity[bucketIndex];
        bucket.NewCount++;
        switch (response.Status)
        {
            case ResponseStatus.Sent:
                bucket.SentCount++;
                break;
            case ResponseStatus.Duplicate:
                bucket.DuplicateCount++;
                break;
            case ResponseStatus.Error:
                bucket.ErrorCount++;
                break;
        }

        Activity[bucketIndex] = new ActivityPoint
        {
            Label = bucket.Label,
            NewCount = bucket.NewCount,
            SentCount = bucket.SentCount,
            DuplicateCount = bucket.DuplicateCount,
            ErrorCount = bucket.ErrorCount
        };
    }

    private void ApplyStats(DashboardStats stats)
    {
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

        EnsureActivityBuckets();
    }

    private void ApplyActiveAdsSnapshot()
    {
        var snapshot = _monitoringService.GetActiveAdsSnapshot()
            .OrderByDescending(static ad => ad.Views)
            .ThenBy(static ad => ad.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        void UpdateCollection()
        {
            for (var index = 0; index < snapshot.Count; index++)
            {
                var desired = snapshot[index];
                if (index < ActiveAds.Count && string.Equals(ActiveAds[index].Id, desired.Id, StringComparison.Ordinal))
                {
                    if (!AreEquivalent(ActiveAds[index], desired))
                    {
                        ActiveAds[index] = CloneAd(desired);
                    }

                    continue;
                }

                var existingIndex = FindAdIndex(desired.Id, index + 1);
                if (existingIndex >= 0)
                {
                    ActiveAds.Move(existingIndex, index);
                    if (!AreEquivalent(ActiveAds[index], desired))
                    {
                        ActiveAds[index] = CloneAd(desired);
                    }

                    continue;
                }

                ActiveAds.Insert(index, CloneAd(desired));
            }

            while (ActiveAds.Count > snapshot.Count)
            {
                ActiveAds.RemoveAt(ActiveAds.Count - 1);
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

    private void EnsureActivityBuckets()
    {
        if (Activity.Count == 8)
        {
            return;
        }

        Activity.Clear();
        for (var hour = 0; hour < 24; hour += 3)
        {
            Activity.Add(new ActivityPoint { Label = $"{hour:00}:00" });
        }
    }

    private int FindAdIndex(string id, int startIndex)
    {
        for (var index = startIndex; index < ActiveAds.Count; index++)
        {
            if (string.Equals(ActiveAds[index].Id, id, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool AreEquivalent(AvitoAdStatus left, AvitoAdStatus right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && string.Equals(left.City, right.City, StringComparison.Ordinal)
        && string.Equals(left.Salary, right.Salary, StringComparison.Ordinal)
        && left.Views == right.Views
        && left.Contacts == right.Contacts
        && left.Favorites == right.Favorites
        && string.Equals(left.Status, right.Status, StringComparison.Ordinal)
        && string.Equals(left.DeleteDate, right.DeleteDate, StringComparison.Ordinal)
        && left.DaysOnAvito == right.DaysOnAvito;

    private static AvitoAdStatus CloneAd(AvitoAdStatus ad) => new()
    {
        Id = ad.Id,
        Title = ad.Title,
        City = ad.City,
        Salary = ad.Salary,
        Views = ad.Views,
        Contacts = ad.Contacts,
        Favorites = ad.Favorites,
        Status = ad.Status,
        DeleteDate = ad.DeleteDate,
        DaysOnAvito = ad.DaysOnAvito
    };
}
