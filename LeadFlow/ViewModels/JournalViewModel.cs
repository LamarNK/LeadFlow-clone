using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Data;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
using LeadFlow.Services;

namespace LeadFlow.ViewModels;

public partial class JournalViewModel(AppRepository repository, ICsvExportService csvExportService) : ObservableObject
{
    public ObservableCollection<CandidateResponse> Items { get; } = [];
    public ObservableCollection<LogFileEntry> LogEntries { get; } = [];

    [ObservableProperty]
    private CandidateResponse? selectedItem;

    [ObservableProperty]
    private LogFileEntry? selectedLogEntry;

    [ObservableProperty]
    private string exportPath = string.Empty;

    [ObservableProperty]
    private string logDirectoryPath = GlobalLogger.ResolveLogDirectoryForService("LeadFlow");

    [ObservableProperty]
    private int logEntriesCount;

    public int JournalItemsCount => Items.Count;
    public int SentJournalItemsCount => Items.Count(x => x.Status == ResponseStatus.Sent);
    public int ErrorJournalItemsCount => Items.Count(x => x.Status == ResponseStatus.Error);
    public int TamperedLogEntriesCount => LogEntries.Count(x => x.IsTampered);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var items = await repository.GetRecentResponsesAsync(100, CancellationToken.None);
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

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
    }

    [RelayCommand]
    public async Task ExportAsync()
    {
        ExportPath = await csvExportService.ExportJournalAsync(Items, CancellationToken.None);
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
}
