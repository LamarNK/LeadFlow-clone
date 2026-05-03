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
    private string exportPath = string.Empty;

    [ObservableProperty]
    private string logDirectoryPath = GlobalLogger.ResolveLogDirectoryForService("LeadFlow");

    [ObservableProperty]
    private int logEntriesCount;

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

        LogEntriesCount = LogEntries.Count;
    }

    [RelayCommand]
    public async Task ExportAsync()
    {
        ExportPath = await csvExportService.ExportJournalAsync(Items, CancellationToken.None);
    }
}
