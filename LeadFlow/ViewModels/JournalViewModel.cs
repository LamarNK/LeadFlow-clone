using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Data;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
using LeadFlow.Services;

namespace LeadFlow.ViewModels;

public partial class JournalViewModel : ObservableObject
{
    private readonly AppRepository _repository;
    private readonly ICsvExportService _csvExportService;
    private readonly ISettingsService _settingsService;

    public ObservableCollection<CandidateResponse> Items { get; } = [];
    public ObservableCollection<LogFileEntry> LogEntries { get; } = [];
    public ICollectionView LogEntriesView { get; }

    [ObservableProperty]
    private CandidateResponse? selectedItem;

    [ObservableProperty]
    private LogFileEntry? selectedLogEntry;

    [ObservableProperty]
    private string exportPath = string.Empty;

    [ObservableProperty]
    private int logEntriesCount;

    [ObservableProperty]
    private string logSearchText = string.Empty;

    [ObservableProperty]
    private string selectedLogLevelFilter = "Все";

    [ObservableProperty]
    private DateTime? selectedLogDate;

    public bool ShowJournalResponsesEmpty => Items.Count == 0;

    public string JournalResponsesEmptyHint =>
        "Пока нет записей. Запустите мониторинг или нажмите «Обновить».";

    public bool ShowJournalLogsEmpty => LogEntriesView.IsEmpty;

    public string JournalLogsEmptyHint => LogEntries.Count == 0
        ? "Пока нет записей в логе."
        : "Нет записей за выбранные условия. Сбросьте дату или уровень.";

    public JournalViewModel(
        AppRepository repository,
        ICsvExportService csvExportService,
        ISettingsService settingsService)
    {
        _repository = repository;
        _csvExportService = csvExportService;
        _settingsService = settingsService;
        LogEntriesView = CollectionViewSource.GetDefaultView(LogEntries);
        LogEntriesView.Filter = FilterLogEntry;
        Items.CollectionChanged += OnItemsCollectionChanged;
        LogEntries.CollectionChanged += OnLogEntriesCollectionChanged;
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        NotifyJournalResponsesEmpty();

    private void OnLogEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        NotifyJournalLogsEmpty();

    private void NotifyJournalResponsesEmpty()
    {
        OnPropertyChanged(nameof(ShowJournalResponsesEmpty));
        OnPropertyChanged(nameof(JournalResponsesEmptyHint));
    }

    private void NotifyJournalLogsEmpty()
    {
        OnPropertyChanged(nameof(ShowJournalLogsEmpty));
        OnPropertyChanged(nameof(JournalLogsEmptyHint));
    }

    public int JournalItemsCount => Items.Count;
    public int SentJournalItemsCount => Items.Count(x => x.Status == ResponseStatus.Sent);
    public int ErrorJournalItemsCount => Items.Count(x => x.Status == ResponseStatus.Error);
    public int TamperedLogEntriesCount => LogEntries.Count(x => x.IsTampered);
    public IReadOnlyList<string> LogLevelFilters { get; } = ["Все", "Info", "Debug", "Warning", "Error"];

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var items = await _repository.GetRecentResponsesAsync(100, CancellationToken.None);
        ReplaceItems(items);

        var logs = await GlobalLogger.Instance.SearchLogsAsync(service: "LeadFlow");
        LogEntries.Clear();
        foreach (var log in logs)
        {
            LogEntries.Add(log);
        }

        OnPropertyChanged(nameof(JournalItemsCount));
        OnPropertyChanged(nameof(SentJournalItemsCount));
        OnPropertyChanged(nameof(ErrorJournalItemsCount));
        OnPropertyChanged(nameof(TamperedLogEntriesCount));
        LogEntriesCount = LogEntries.Count;
        SelectedItem ??= Items.FirstOrDefault();
        SelectedLogEntry ??= LogEntries.FirstOrDefault();
        LogEntriesView.Refresh();
        NotifyJournalLogsEmpty();
        NotifyJournalResponsesEmpty();
    }

    public void ApplyProcessedResponse(CandidateResponse response)
    {
        var existingIndex = Items
            .Select((item, index) => new { item, index })
            .FirstOrDefault(x => x.item.Id == response.Id)
            ?.index;

        if (existingIndex.HasValue)
        {
            Items.RemoveAt(existingIndex.Value);
        }

        var insertIndex = 0;
        while (insertIndex < Items.Count && Items[insertIndex].CreatedAt > response.CreatedAt)
        {
            insertIndex++;
        }

        Items.Insert(insertIndex, response);
        while (Items.Count > 100)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        if (SelectedItem?.Id == response.Id || SelectedItem is null)
        {
            SelectedItem = response;
        }

        NotifyJournalCountersChanged();
        NotifyJournalResponsesEmpty();
    }

    [RelayCommand]
    public async Task ExportAsync()
    {
        ExportPath = await _csvExportService.ExportJournalAsync(Items, CancellationToken.None);
    }

    [RelayCommand]
    private void CopySelectedLogMessage()
    {
        if (!string.IsNullOrWhiteSpace(SelectedLogEntry?.Message))
        {
            Clipboard.SetText(SelectedLogEntry.Message);
        }
    }

    [RelayCommand]
    private void CopySelectedLogDetails()
    {
        if (!string.IsNullOrWhiteSpace(SelectedLogDetails))
        {
            Clipboard.SetText(SelectedLogDetails);
        }
    }

    [RelayCommand]
    private void CopySelectedLogRow()
    {
        if (SelectedLogEntry is null)
        {
            return;
        }

        var row = $"{SelectedLogEntry.Timestamp.ToLocalTime():dd.MM.yyyy HH:mm:ss}\t{SelectedLogEntry.Level}\t{SelectedLogEntry.Prefix}\t{SelectedLogEntry.Message}\t{SelectedLogEntry.TraceId}\t{SelectedLogEntry.Properties}";
        Clipboard.SetText(row);
    }

    [RelayCommand]
    private void ClearLogDateFilter()
    {
        SelectedLogDate = null;
    }

    public string SelectedJournalStatusText => SelectedItem?.Status switch
    {
        ResponseStatus.New => "Новый отклик",
        ResponseStatus.InProgress => "В обработке",
        ResponseStatus.Sent => "Отправлен в Bitrix24",
        ResponseStatus.Duplicate => "Дубль",
        ResponseStatus.Error => "Ошибка",
        ResponseStatus.ActionRequired => "Нужно действие",
        _ => "Запись не выбрана"
    };

    public string SelectedJournalErrorText => string.IsNullOrWhiteSpace(SelectedItem?.ErrorMessage)
        ? "Ошибок не было"
        : SelectedItem!.ErrorMessage;

    public string SelectedLogSummary => SelectedLogEntry is null
        ? "Запись лога не выбрана"
        : $"{SelectedLogEntry.Timestamp.ToLocalTime():dd.MM.yyyy HH:mm:ss} • {SelectedLogEntry.Level}";

    public string SelectedLogDetails => string.IsNullOrWhiteSpace(SelectedLogEntry?.Properties)
        ? "Дополнительные свойства отсутствуют"
        : SelectedLogEntry!.Properties;

    partial void OnSelectedItemChanged(CandidateResponse? value)
    {
        OnPropertyChanged(nameof(SelectedJournalStatusText));
        OnPropertyChanged(nameof(SelectedJournalErrorText));
    }

    partial void OnSelectedLogEntryChanged(LogFileEntry? value)
    {
        OnPropertyChanged(nameof(SelectedLogSummary));
        OnPropertyChanged(nameof(SelectedLogDetails));
    }

    partial void OnLogSearchTextChanged(string value)
    {
        LogEntriesView.Refresh();
        NotifyJournalLogsEmpty();
    }

    partial void OnSelectedLogLevelFilterChanged(string value)
    {
        LogEntriesView.Refresh();
        NotifyJournalLogsEmpty();
    }

    partial void OnSelectedLogDateChanged(DateTime? value)
    {
        LogEntriesView.Refresh();
        NotifyJournalLogsEmpty();
    }

    private bool FilterLogEntry(object obj)
    {
        if (obj is not LogFileEntry item)
        {
            return false;
        }

        var levelMatches = SelectedLogLevelFilter == "Все"
            || string.Equals(item.Level.ToString(), SelectedLogLevelFilter, StringComparison.OrdinalIgnoreCase);
        if (!levelMatches)
        {
            return false;
        }

        var dateMatches = !SelectedLogDate.HasValue || item.Timestamp.ToLocalTime().Date == SelectedLogDate.Value.Date;
        if (!dateMatches)
        {
            return false;
        }

        var search = LogSearchText.Trim();
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return (item.Message?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.Prefix?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.TraceId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.Properties?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void ReplaceItems(IEnumerable<CandidateResponse> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        NotifyJournalCountersChanged();
        NotifyJournalResponsesEmpty();
    }

    private void NotifyJournalCountersChanged()
    {
        OnPropertyChanged(nameof(JournalItemsCount));
        OnPropertyChanged(nameof(SentJournalItemsCount));
        OnPropertyChanged(nameof(ErrorJournalItemsCount));
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
