using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow;
using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services;
using LeadFlow.Services.Avito;
using LeadFlow.Services.Bitrix;

namespace LeadFlow.ViewModels;

public partial class MonitoringViewModel : ObservableObject
{
    private const int MonitoringListMaxItems = 1000;
    private const string StatusFilterAll = "Все";
    private const string StatusFilterExcludeDuplicates = "Все, кроме дублей";
    private const string AllVacanciesLabel = "Все вакансии";
    private const string AllCitiesLabel = "Все города";
    private const string AllAccountsLabel = "Все аккаунты";

    public event EventHandler<CandidateResponse?>? SelectedResponseChanged;

    public ObservableCollection<CandidateResponse> Responses { get; } = [];

    public ObservableCollection<string> VacancyFilterOptions { get; } = [];

    public ObservableCollection<string> CityFilterOptions { get; } = [];

    public ObservableCollection<string> AccountFilterOptions { get; } = [];

    public ICollectionView ResponsesView { get; }

    [ObservableProperty]
    private CandidateResponse? selectedResponse;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string selectedStatusFilter = StatusFilterAll;

    [ObservableProperty]
    private DateTime? selectedResponseDate;

    [ObservableProperty]
    private string selectedVacancyFilter = AllVacanciesLabel;

    [ObservableProperty]
    private string selectedCityFilter = AllCitiesLabel;

    [ObservableProperty]
    private string selectedAccountFilter = AllAccountsLabel;

    [ObservableProperty]
    private string ageFilterMinText = string.Empty;

    [ObservableProperty]
    private string ageFilterMaxText = string.Empty;

    public int ExtraFiltersActiveCount =>
        (IsSpecificVacancyFilter ? 1 : 0)
        + (IsSpecificCityFilter ? 1 : 0)
        + (IsSpecificAccountFilter ? 1 : 0)
        + (HasAgeRangeFilter ? 1 : 0);

    public string ExtraFiltersHeaderSuffix => ExtraFiltersActiveCount > 0 ? $" ({ExtraFiltersActiveCount})" : string.Empty;

    private bool IsSpecificVacancyFilter =>
        !string.IsNullOrEmpty(SelectedVacancyFilter) && !string.Equals(SelectedVacancyFilter, AllVacanciesLabel, StringComparison.Ordinal);

    private bool IsSpecificCityFilter =>
        !string.IsNullOrEmpty(SelectedCityFilter) && !string.Equals(SelectedCityFilter, AllCitiesLabel, StringComparison.Ordinal);

    private bool IsSpecificAccountFilter =>
        !string.IsNullOrEmpty(SelectedAccountFilter) && !string.Equals(SelectedAccountFilter, AllAccountsLabel, StringComparison.Ordinal);

    private bool HasAgeRangeFilter =>
        int.TryParse(AgeFilterMinText.Trim(), out _)
        || int.TryParse(AgeFilterMaxText.Trim(), out _);

    public int TotalResponsesCount => Responses.Count;
    public int NewResponsesCount => Responses.Count(x => x.Status == ResponseStatus.New);
    public int SentResponsesCount => Responses.Count(x => x.Status == ResponseStatus.Sent);
    public int ErrorResponsesCount => Responses.Count(x => x.Status == ResponseStatus.Error);

    public bool ShowMonitoringResponsesEmpty => ResponsesView.IsEmpty;

    public string MonitoringResponsesEmptyHint => Responses.Count == 0
        ? "Пока нет откликов. Запустите мониторинг на главном экране или нажмите «Обновить»."
        : "Нет откликов в текущем фильтре. Смените дату, статус, доп. фильтры или строку поиска.";

    private readonly AppRepository _repository;
    private readonly ISettingsService _settingsService;
    private readonly IWindowService _windowService;
    private readonly IBitrixClient _bitrixClient;
    private readonly IDuplicateService _duplicateService;
    private bool _suppressFilterLookupRefresh;

    public MonitoringViewModel(
        AppRepository repository,
        ISettingsService settingsService,
        IWindowService windowService,
        IBitrixClient bitrixClient,
        IDuplicateService duplicateService) : base()
    {
        _repository = repository;
        _settingsService = settingsService;
        _windowService = windowService;
        _bitrixClient = bitrixClient;
        _duplicateService = duplicateService;
        ResponsesView = CollectionViewSource.GetDefaultView(Responses);
        ResponsesView.Filter = FilterResponse;
        Responses.CollectionChanged += OnResponsesCollectionChanged;
        RefreshFilterLookups();
    }

    private void OnResponsesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressFilterLookupRefresh)
        {
            RefreshFilterLookups();
        }

        NotifyMonitoringListUi();
    }

    private void NotifyMonitoringListUi()
    {
        OnPropertyChanged(nameof(ShowMonitoringResponsesEmpty));
        OnPropertyChanged(nameof(MonitoringResponsesEmptyHint));
    }

    partial void OnSelectedResponseChanged(CandidateResponse? value)
    {
        OnPropertyChanged(nameof(SelectedResponseStatusText));
        OnPropertyChanged(nameof(SelectedResponseProcessedText));
        OnPropertyChanged(nameof(SelectedResponseErrorText));
        OpenResponseChatInAvitoBrowserCommand.NotifyCanExecuteChanged();
        OpenResponseVacancyInAvitoBrowserCommand.NotifyCanExecuteChanged();
        CopyResponsePhoneCommand.NotifyCanExecuteChanged();
        CopyResponseMessengerUrlCommand.NotifyCanExecuteChanged();
        CopyResponseVacancyUrlCommand.NotifyCanExecuteChanged();
        CopyResponseCardSummaryCommand.NotifyCanExecuteChanged();
        OpenResponseInBitrixCommand.NotifyCanExecuteChanged();
        DeleteSelectedResponseCommand.NotifyCanExecuteChanged();
        SendToBitrixManuallyCommand.NotifyCanExecuteChanged();
        SelectedResponseChanged?.Invoke(this, value);
    }

    partial void OnSearchTextChanged(string value) => RefreshMonitoringFilter();

    partial void OnSelectedStatusFilterChanged(string value) => RefreshMonitoringFilter();

    partial void OnSelectedResponseDateChanged(DateTime? value) => RefreshMonitoringFilter();

    partial void OnSelectedVacancyFilterChanged(string value) => NotifyExtraFiltersAndRefresh();

    partial void OnSelectedCityFilterChanged(string value) => NotifyExtraFiltersAndRefresh();

    partial void OnSelectedAccountFilterChanged(string value) => NotifyExtraFiltersAndRefresh();

    partial void OnAgeFilterMinTextChanged(string value) => NotifyExtraFiltersAndRefresh();

    partial void OnAgeFilterMaxTextChanged(string value) => NotifyExtraFiltersAndRefresh();

    private void NotifyExtraFiltersAndRefresh()
    {
        OnPropertyChanged(nameof(ExtraFiltersActiveCount));
        OnPropertyChanged(nameof(ExtraFiltersHeaderSuffix));
        RefreshMonitoringFilter();
    }

    private void RefreshMonitoringFilter()
    {
        ResponsesView.Refresh();
        var visible = ResponsesView.Cast<CandidateResponse>().ToList();
        if (SelectedResponse is not null && visible.Contains(SelectedResponse))
        {
            NotifyMonitoringListUi();
            return;
        }

        SelectedResponse = visible.FirstOrDefault();
        NotifyMonitoringListUi();
    }

    [RelayCommand]
    private void ClearMonitoringFilters()
    {
        SearchText = string.Empty;
        SelectedStatusFilter = StatusFilterAll;
        SelectedResponseDate = null;
        SelectedVacancyFilter = AllVacanciesLabel;
        SelectedCityFilter = AllCitiesLabel;
        SelectedAccountFilter = AllAccountsLabel;
        AgeFilterMinText = string.Empty;
        AgeFilterMaxText = string.Empty;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var items = await _repository.GetRecentResponsesAsync(MonitoringListMaxItems, CancellationToken.None);
        ReplaceResponses(items);
    }

    public void ApplyProcessedResponse(CandidateResponse response)
    {
        var existingIndex = Responses
            .Select((item, index) => new { item, index })
            .FirstOrDefault(x => x.item.Id == response.Id)
            ?.index;

        if (existingIndex.HasValue)
        {
            Responses.RemoveAt(existingIndex.Value);
        }

        var insertIndex = 0;
        while (insertIndex < Responses.Count && Responses[insertIndex].CreatedAt > response.CreatedAt)
        {
            insertIndex++;
        }

        Responses.Insert(insertIndex, response);
        while (Responses.Count > MonitoringListMaxItems)
        {
            Responses.RemoveAt(Responses.Count - 1);
        }

        if (SelectedResponse?.Id == response.Id || SelectedResponse is null)
        {
            SelectedResponse = response;
        }

        NotifyCountersChanged();
        RefreshMonitoringFilter();
    }

    public string SelectedResponseStatusText => SelectedResponse is null
        ? "Отклик не выбран"
        : ResponseStatusFormatting.DetailDescription(SelectedResponse.Status);

    public string SelectedResponseProcessedText => SelectedResponse?.ProcessedAt is DateTime processedAt
        ? processedAt.ToLocalTimeFromStoredUtc().ToString("dd.MM.yyyy HH:mm")
        : "Ещё не обработан";

    public string SelectedResponseErrorText => string.IsNullOrWhiteSpace(SelectedResponse?.ErrorMessage)
        ? "Ошибок не зафиксировано"
        : SelectedResponse!.ErrorMessage;

    private bool FilterResponse(object obj)
    {
        if (obj is not CandidateResponse item)
        {
            return false;
        }

        var statusMatches = SelectedStatusFilter switch
        {
            StatusFilterAll => true,
            StatusFilterExcludeDuplicates => item.Status != ResponseStatus.Duplicate,
            _ => item.Status.ToString() == SelectedStatusFilter
        };
        if (!statusMatches)
        {
            return false;
        }

        if (SelectedResponseDate.HasValue && item.CreatedAt.ToLocalTimeFromStoredUtc().Date != SelectedResponseDate.Value.Date)
        {
            return false;
        }

        if (IsSpecificVacancyFilter)
        {
            var v = item.Vacancy.Trim();
            if (!string.Equals(v, SelectedVacancyFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (IsSpecificCityFilter)
        {
            var c = item.City.Trim();
            if (!string.Equals(c, SelectedCityFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (IsSpecificAccountFilter)
        {
            var a = item.AccountName.Trim();
            if (!string.Equals(a, SelectedAccountFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (HasAgeRangeFilter)
        {
            if (!TryParseAgeBounds(out var minAge, out var maxAge))
            {
                return true;
            }

            if (!item.Age.HasValue)
            {
                return false;
            }

            var age = item.Age.Value;
            if (minAge.HasValue && age < minAge.Value)
            {
                return false;
            }

            if (maxAge.HasValue && age > maxAge.Value)
            {
                return false;
            }
        }

        var search = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return item.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.PhoneRaw.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.Vacancy.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.City.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.AccountName.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshFilterLookups()
    {
        var vacancyPreserve = SelectedVacancyFilter;
        var cityPreserve = SelectedCityFilter;
        var accountPreserve = SelectedAccountFilter;

        VacancyFilterOptions.Clear();
        VacancyFilterOptions.Add(AllVacanciesLabel);
        foreach (var v in Responses
                     .Select(r => r.Vacancy.Trim())
                     .Where(s => !string.IsNullOrEmpty(s))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            VacancyFilterOptions.Add(v);
        }

        CityFilterOptions.Clear();
        CityFilterOptions.Add(AllCitiesLabel);
        foreach (var c in Responses
                     .Select(r => r.City.Trim())
                     .Where(s => !string.IsNullOrEmpty(s))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            CityFilterOptions.Add(c);
        }

        AccountFilterOptions.Clear();
        AccountFilterOptions.Add(AllAccountsLabel);
        foreach (var a in Responses
                     .Select(r => r.AccountName.Trim())
                     .Where(s => !string.IsNullOrEmpty(s))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            AccountFilterOptions.Add(a);
        }

        if (!VacancyFilterOptions.Contains(vacancyPreserve))
        {
            vacancyPreserve = AllVacanciesLabel;
        }

        if (!CityFilterOptions.Contains(cityPreserve))
        {
            cityPreserve = AllCitiesLabel;
        }

        if (!AccountFilterOptions.Contains(accountPreserve))
        {
            accountPreserve = AllAccountsLabel;
        }

        if (!string.Equals(SelectedVacancyFilter, vacancyPreserve, StringComparison.Ordinal))
        {
            SelectedVacancyFilter = vacancyPreserve;
        }

        if (!string.Equals(SelectedCityFilter, cityPreserve, StringComparison.Ordinal))
        {
            SelectedCityFilter = cityPreserve;
        }

        if (!string.Equals(SelectedAccountFilter, accountPreserve, StringComparison.Ordinal))
        {
            SelectedAccountFilter = accountPreserve;
        }
    }

    private bool TryParseAgeBounds(out int? minAge, out int? maxAge)
    {
        minAge = null;
        maxAge = null;
        var minOk = int.TryParse(AgeFilterMinText.Trim(), out var minV);
        var maxOk = int.TryParse(AgeFilterMaxText.Trim(), out var maxV);
        if (minOk)
        {
            minAge = minV;
        }

        if (maxOk)
        {
            maxAge = maxV;
        }

        return minOk || maxOk;
    }

    private void ReplaceResponses(IEnumerable<CandidateResponse> items)
    {
        _suppressFilterLookupRefresh = true;
        try
        {
            Responses.Clear();
            foreach (var item in items)
            {
                Responses.Add(item);
            }
        }
        finally
        {
            _suppressFilterLookupRefresh = false;
            RefreshFilterLookups();
        }

        NotifyCountersChanged();
        RefreshMonitoringFilter();
    }

    private void NotifyCountersChanged()
    {
        OnPropertyChanged(nameof(TotalResponsesCount));
        OnPropertyChanged(nameof(NewResponsesCount));
        OnPropertyChanged(nameof(SentResponsesCount));
        OnPropertyChanged(nameof(ErrorResponsesCount));
    }

    private bool CanCopyResponsePhone(CandidateResponse? r) => CandidateResponseUiActions.CanCopyPhone(r);

    [RelayCommand(CanExecute = nameof(CanCopyResponsePhone))]
    private void CopyResponsePhone(CandidateResponse? response) =>
        CandidateResponseUiActions.TryCopyPhone(response);

    private bool CanOpenResponseInBitrix(CandidateResponse? r) => CandidateResponseUiActions.CanOpenBitrix(r);

    [RelayCommand(CanExecute = nameof(CanOpenResponseInBitrix))]
    private async Task OpenResponseInBitrixAsync(CandidateResponse? response)
    {
        await CandidateResponseUiActions.TryOpenBitrixAsync(response, _settingsService, CancellationToken.None);
    }

    private bool CanOpenResponseChatInAvitoBrowser(CandidateResponse? r) =>
        r is not null && !string.IsNullOrWhiteSpace(r.MessengerUrl);

    [RelayCommand(CanExecute = nameof(CanOpenResponseChatInAvitoBrowser))]
    private async Task OpenResponseChatInAvitoBrowserAsync(CandidateResponse? response)
    {
        if (response is null || string.IsNullOrWhiteSpace(response.MessengerUrl))
        {
            return;
        }

        var owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            return;
        }

        var account = await _repository.GetAccountByIdAsync(response.AccountId, CancellationToken.None);
        if (account is null)
        {
            return;
        }

        await _windowService.ShowAvitoProfileAsync(owner, account, response.MessengerUrl, CancellationToken.None);
    }

    private static bool HasSpecificVacancyUrl(CandidateResponse? r)
    {
        var url = r?.VacancyUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var normalized = url.Trim();
        if (string.Equals(normalized, AvitoResponseSource.CandidatesPageUrl, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !normalized.Contains("/profile/candidates", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanOpenResponseVacancyInAvitoBrowser(CandidateResponse? r) => HasSpecificVacancyUrl(r);

    [RelayCommand(CanExecute = nameof(CanOpenResponseVacancyInAvitoBrowser))]
    private async Task OpenResponseVacancyInAvitoBrowserAsync(CandidateResponse? response)
    {
        if (response is null || !HasSpecificVacancyUrl(response))
        {
            return;
        }

        var owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            return;
        }

        var account = await _repository.GetAccountByIdAsync(response.AccountId, CancellationToken.None);
        if (account is null)
        {
            return;
        }

        await _windowService.ShowAvitoProfileAsync(owner, account, response.VacancyUrl.Trim(), CancellationToken.None);
    }

    private bool CanCopyResponseMessengerUrl(CandidateResponse? r) =>
        CandidateResponseUiActions.CanCopyMessengerUrl(r);

    [RelayCommand(CanExecute = nameof(CanCopyResponseMessengerUrl))]
    private void CopyResponseMessengerUrl(CandidateResponse? response) =>
        CandidateResponseUiActions.TryCopyMessengerUrl(response);

    private bool CanCopyResponseVacancyUrl(CandidateResponse? r) =>
        CandidateResponseUiActions.CanCopyVacancyUrl(r);

    [RelayCommand(CanExecute = nameof(CanCopyResponseVacancyUrl))]
    private void CopyResponseVacancyUrl(CandidateResponse? response) =>
        CandidateResponseUiActions.TryCopyVacancyUrl(response);

    private bool CanCopyResponseCardSummary(CandidateResponse? r) =>
        CandidateResponseUiActions.CanCopyCardSummary(r);

    [RelayCommand(CanExecute = nameof(CanCopyResponseCardSummary))]
    private void CopyResponseCardSummary(CandidateResponse? response) =>
        CandidateResponseUiActions.TryCopyCardSummary(response);

    private static bool CanSendToBitrixManually(CandidateResponse? r) =>
        r is not null
        && (r.Status == ResponseStatus.ActionRequired || r.Status == ResponseStatus.Error)
        && string.IsNullOrWhiteSpace(r.BitrixEntityId);

    [RelayCommand(CanExecute = nameof(CanSendToBitrixManually))]
    private async Task SendToBitrixManuallyAsync(CandidateResponse? response)
    {
        if (response is null || !string.IsNullOrWhiteSpace(response.BitrixEntityId))
        {
            return;
        }

        var settings = await _settingsService.LoadAsync(CancellationToken.None);
        var duplicate = await _duplicateService.CheckAsync(response, settings, CancellationToken.None);

        if (duplicate.IsDuplicate)
        {
            response.Status = ResponseStatus.Duplicate;
            response.ProcessedAt = DateTime.UtcNow;
            response.ErrorMessage = string.Empty;
            await _repository.SaveCandidateAsync(response, CancellationToken.None);
            await _repository.AddLogAsync(new ProcessingLogItem
            {
                CandidateResponseId = response.Id,
                AccountId = response.AccountId,
                Level = "Info",
                Message = "Ручная проверка: дубль",
                Details = duplicate.Summary
            }, CancellationToken.None);
            ApplyProcessedResponse(response);
            NotifyCountersChanged();
            return;
        }

        if (duplicate.ShouldDeferBitrixSend)
        {
            MessageBox.Show(
                duplicate.Summary,
                "Проверка дублей в Bitrix24",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        const string sendDisabledMessage = "Тестовый режим: отправка в Bitrix24 временно отключена.";
        MessageBox.Show(
            sendDisabledMessage,
            "Bitrix24",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        response.ProcessedAt = DateTime.UtcNow;
        response.Status = ResponseStatus.ActionRequired;
        response.ErrorMessage = sendDisabledMessage;
        await _repository.AddLogAsync(new ProcessingLogItem
        {
            CandidateResponseId = response.Id,
            AccountId = response.AccountId,
            Level = "Warning",
            Message = "Ручная отправка в Bitrix24 отключена",
            Details = sendDisabledMessage
        }, CancellationToken.None);
        await _repository.SaveCandidateAsync(response, CancellationToken.None);
        ApplyProcessedResponse(response);
        NotifyCountersChanged();
    }

    private bool CanDeleteSelectedResponse(CandidateResponse? r) => r is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedResponse))]
    private async Task DeleteSelectedResponseAsync(CandidateResponse? response)
    {
        if (response is null)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            $"Удалить отклик «{response.FullName}» из журнала? Действие нельзя отменить.",
            "Удаление отклика",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        await _repository.DeleteCandidateResponseAsync(response.Id, CancellationToken.None);
        Responses.Remove(response);
        NotifyCountersChanged();
        RefreshMonitoringFilter();
    }
}
