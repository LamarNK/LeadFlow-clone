using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Data;
using LeadFlow.Models;

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

    public MonitoringViewModel(AppRepository repository) : base()
    {
        ResponsesView = CollectionViewSource.GetDefaultView(Responses);
        ResponsesView.Filter = FilterResponse;
        _repository = repository;
    }

    private readonly AppRepository _repository;

    partial void OnSelectedResponseChanged(CandidateResponse? value)
    {
        OnPropertyChanged(nameof(SelectedResponseStatusText));
        OnPropertyChanged(nameof(SelectedResponseProcessedText));
        OnPropertyChanged(nameof(SelectedResponseErrorText));
        SelectedResponseChanged?.Invoke(this, value);
    }
    partial void OnSearchTextChanged(string value) => ResponsesView.Refresh();
    partial void OnSelectedStatusFilterChanged(string value) => ResponsesView.Refresh();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var items = await _repository.GetRecentResponsesAsync(100, CancellationToken.None);
        Responses.Clear();
        foreach (var item in items)
        {
            Responses.Add(item);
        }

        OnPropertyChanged(nameof(TotalResponsesCount));
        OnPropertyChanged(nameof(NewResponsesCount));
        OnPropertyChanged(nameof(SentResponsesCount));
        OnPropertyChanged(nameof(ErrorResponsesCount));
        SelectedResponse ??= Responses.FirstOrDefault();
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
}
