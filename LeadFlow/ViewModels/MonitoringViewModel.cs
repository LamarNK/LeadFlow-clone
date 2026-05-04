using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services;

namespace LeadFlow.ViewModels;

public partial class MonitoringViewModel : ObservableObject
{
    public event EventHandler<CandidateResponse?>? SelectedResponseChanged;

    public ObservableCollection<CandidateResponse> Responses { get; } = [];

    public ICollectionView ResponsesView { get; }

    [ObservableProperty]
    private CandidateResponse? selectedResponse;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string selectedStatusFilter = "Все";

    public int TotalResponsesCount => Responses.Count;
    public int NewResponsesCount => Responses.Count(x => x.Status == ResponseStatus.New);
    public int SentResponsesCount => Responses.Count(x => x.Status == ResponseStatus.Sent);
    public int ErrorResponsesCount => Responses.Count(x => x.Status == ResponseStatus.Error);

    public bool ShowMonitoringResponsesEmpty => ResponsesView.IsEmpty;

    public string MonitoringResponsesEmptyHint => Responses.Count == 0
        ? "Пока нет откликов. Запустите мониторинг на главном экране или нажмите «Обновить»."
        : "Нет откликов в текущем фильтре. Смените статус или строку поиска.";

    private readonly AppRepository _repository;
    private readonly ISettingsService _settingsService;

    public MonitoringViewModel(AppRepository repository, ISettingsService settingsService) : base()
    {
        _repository = repository;
        _settingsService = settingsService;
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
        SelectedResponseChanged?.Invoke(this, value);
    }

    partial void OnSearchTextChanged(string value)
    {
        ResponsesView.Refresh();
        NotifyMonitoringListUi();
    }

    partial void OnSelectedStatusFilterChanged(string value)
    {
        ResponsesView.Refresh();
        NotifyMonitoringListUi();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var items = await _repository.GetRecentResponsesAsync(100, CancellationToken.None);
        ReplaceResponses(items);
        SelectedResponse ??= Responses.FirstOrDefault();
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
        ResponsesView.Refresh();
        NotifyMonitoringListUi();
    }

    public string SelectedResponseStatusText => SelectedResponse?.Status switch
    {
        ResponseStatus.New => "Новый отклик",
        ResponseStatus.InProgress => "В обработке",
        ResponseStatus.Sent => "Отправлен в Bitrix24",
        ResponseStatus.Duplicate => "Найден дубль",
        ResponseStatus.Error => "Ошибка обработки",
        ResponseStatus.ActionRequired => "Нужно действие",
        _ => "Отклик не выбран"
    };

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

        var statusMatches = SelectedStatusFilter == "Все" || item.Status.ToString() == SelectedStatusFilter;
        var search = SearchText.Trim();
        var textMatches = string.IsNullOrWhiteSpace(search)
            || item.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.PhoneRaw.Contains(search, StringComparison.OrdinalIgnoreCase);

        return statusMatches && textMatches;
    }

    private void ReplaceResponses(IEnumerable<CandidateResponse> items)
    {
        Responses.Clear();
        foreach (var item in items)
        {
            Responses.Add(item);
        }

        NotifyCountersChanged();
        ResponsesView.Refresh();
        NotifyMonitoringListUi();
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

    private bool CanOpenResponseInAvito(CandidateResponse? r) => CandidateResponseUiActions.CanOpenSource(r);

    [RelayCommand(CanExecute = nameof(CanOpenResponseInAvito))]
    private void OpenResponseInAvito(CandidateResponse? response) =>
        CandidateResponseUiActions.TryOpenSourceUrl(response);

    private bool CanOpenResponseInBitrix(CandidateResponse? r) => CandidateResponseUiActions.CanOpenBitrix(r);

    [RelayCommand(CanExecute = nameof(CanOpenResponseInBitrix))]
    private async Task OpenResponseInBitrixAsync(CandidateResponse? response)
    {
        await CandidateResponseUiActions.TryOpenBitrixAsync(response, _settingsService, CancellationToken.None);
    }
}
