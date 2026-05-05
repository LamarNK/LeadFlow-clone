using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Logging.Audit;

namespace LeadFlow.ViewModels;

public partial class JournalViewModel : ObservableObject
{
    public ObservableCollection<LogFileEntry> LogEntries { get; } = [];
    public ICollectionView LogEntriesView { get; }

    [ObservableProperty]
    private LogFileEntry? selectedLogEntry;

    [ObservableProperty]
    private int logEntriesCount;

    [ObservableProperty]
    private string logSearchText = string.Empty;

    [ObservableProperty]
    private string selectedLogLevelFilter = "Все";

    [ObservableProperty]
    private DateTime? selectedLogDate;

    public bool ShowJournalLogsEmpty => LogEntriesView.IsEmpty;

    public string JournalLogsEmptyHint => LogEntries.Count == 0
        ? "Пока нет записей в логе."
        : "Нет записей за выбранные условия. Сбросьте дату или уровень.";

    public JournalViewModel()
    {
        LogEntriesView = CollectionViewSource.GetDefaultView(LogEntries);
        LogEntriesView.Filter = FilterLogEntry;
        LogEntries.CollectionChanged += OnLogEntriesCollectionChanged;
    }

    private void OnLogEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        NotifyJournalLogsEmpty();

    private void NotifyJournalLogsEmpty()
    {
        OnPropertyChanged(nameof(ShowJournalLogsEmpty));
        OnPropertyChanged(nameof(JournalLogsEmptyHint));
    }

    public int TamperedLogEntriesCount => LogEntries.Count(x => x.IsTampered);

    public IReadOnlyList<string> LogLevelFilters { get; } = ["Все", "Info", "Debug", "Warning", "Error"];

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var logs = await GlobalLogger.Instance.SearchLogsAsync(service: "LeadFlow");
        LogEntries.Clear();
        foreach (var log in logs)
        {
            LogEntries.Add(log);
        }

        OnPropertyChanged(nameof(TamperedLogEntriesCount));
        LogEntriesCount = LogEntries.Count;
        SelectedLogEntry ??= LogEntries.FirstOrDefault();
        LogEntriesView.Refresh();
        NotifyJournalLogsEmpty();
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
    private void ClearLogFilters()
    {
        LogSearchText = string.Empty;
        SelectedLogDate = null;
        SelectedLogLevelFilter = "Все";
    }

    public string SelectedLogDetails => string.IsNullOrWhiteSpace(SelectedLogEntry?.Properties)
        ? "Дополнительные свойства отсутствуют"
        : SelectedLogEntry!.Properties;

    partial void OnSelectedLogEntryChanged(LogFileEntry? value) =>
        OnPropertyChanged(nameof(SelectedLogDetails));

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
}
