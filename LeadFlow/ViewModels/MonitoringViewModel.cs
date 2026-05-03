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

    public MonitoringViewModel(AppRepository repository) : base()
    {
        ResponsesView = CollectionViewSource.GetDefaultView(Responses);
        ResponsesView.Filter = FilterResponse;
        _repository = repository;
    }

    private readonly AppRepository _repository;

    partial void OnSelectedResponseChanged(CandidateResponse? value) => SelectedResponseChanged?.Invoke(this, value);
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

        SelectedResponse ??= Responses.FirstOrDefault();
    }

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
