using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services;
using LeadFlow.Services.Avito;

namespace LeadFlow.ViewModels;

public partial class MonitoringViewModel : ObservableObject
{
    private const string StatusFilterAll = "Все";
    private const string StatusFilterExcludeDuplicates = "Все, кроме дублей";

    public event EventHandler<CandidateResponse?>? SelectedResponseChanged;

    public ObservableCollection<CandidateResponse> Responses { get; } = [];

    public ICollectionView ResponsesView { get; }

    [ObservableProperty]
    private CandidateResponse? selectedResponse;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string selectedStatusFilter = StatusFilterAll;

    [ObservableProperty]
    private DateTime? selectedResponseDate;

    public int TotalResponsesCount => Responses.Count;
    public int NewResponsesCount => Responses.Count(x => x.Status == ResponseStatus.New);
    public int SentResponsesCount => Responses.Count(x => x.Status == ResponseStatus.Sent);
    public int ErrorResponsesCount => Responses.Count(x => x.Status == ResponseStatus.Error);

    public bool ShowMonitoringResponsesEmpty => ResponsesView.IsEmpty;

    public string MonitoringResponsesEmptyHint => Responses.Count == 0
        ? "Пока нет откликов. Запустите мониторинг на главном экране или нажмите «Обновить»."
        : "Нет откликов в текущем фильтре. Смените дату, статус или строку поиска.";

    private readonly AppRepository _repository;
    private readonly ISettingsService _settingsService;
    private readonly IWindowService _windowService;

    public MonitoringViewModel(AppRepository repository, ISettingsService settingsService, IWindowService windowService) : base()
    {
        _repository = repository;
        _settingsService = settingsService;
        _windowService = windowService;
        ResponsesView = CollectionViewSource.GetDefaultView(Responses);
        ResponsesView.Filter = FilterResponse;
        Responses.CollectionChanged += OnResponsesCollectionChanged;
    }

    private void OnResponsesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        NotifyMonitoringListUi();

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
        SelectedResponseChanged?.Invoke(this, value);
    }

    partial void OnSearchTextChanged(string value) => RefreshMonitoringFilter();

    partial void OnSelectedStatusFilterChanged(string value) => RefreshMonitoringFilter();

    partial void OnSelectedResponseDateChanged(DateTime? value) => RefreshMonitoringFilter();

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
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var items = await _repository.GetRecentResponsesAsync(100, CancellationToken.None);
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
        while (Responses.Count > 100)
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
        ? processedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
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

        if (SelectedResponseDate.HasValue && item.CreatedAt.ToLocalTime().Date != SelectedResponseDate.Value.Date)
        {
            return false;
        }

        var search = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return item.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.PhoneRaw.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.Vacancy.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void ReplaceResponses(IEnumerable<CandidateResponse> items)
    {
        Responses.Clear();
        foreach (var item in items)
        {
            Responses.Add(item);
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
        var url = r?.EffectiveVacancyUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return !string.Equals(url.Trim(), AvitoResponseSource.CandidatesPageUrl, StringComparison.OrdinalIgnoreCase);
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

        await _windowService.ShowAvitoProfileAsync(owner, account, response.EffectiveVacancyUrl.Trim(), CancellationToken.None);
    }
}
