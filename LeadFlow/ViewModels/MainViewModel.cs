using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Models;
using LeadFlow.Services;

namespace LeadFlow.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IMonitoringService _monitoringService;
    private readonly IWindowService _windowService;

    public DashboardViewModel Dashboard { get; }
    public MonitoringViewModel Monitoring { get; }
    public CandidateDetailsViewModel CandidateDetails { get; }
    public DuplicateCheckViewModel DuplicateCheck { get; }
    public BitrixIntegrationViewModel BitrixIntegration { get; }
    public JournalViewModel Journal { get; }

    [ObservableProperty]
    private MonitoringStatus systemStatus = MonitoringStatus.Waiting;

    [ObservableProperty]
    private string systemStatusDetails = string.Empty;

    [ObservableProperty]
    private bool isMonitoringActive;

    public MainViewModel(
        IMonitoringService monitoringService,
        IWindowService windowService,
        DashboardViewModel dashboard,
        MonitoringViewModel monitoring,
        CandidateDetailsViewModel candidateDetails,
        DuplicateCheckViewModel duplicateCheck,
        BitrixIntegrationViewModel bitrixIntegration,
        JournalViewModel journal)
    {
        _monitoringService = monitoringService;
        _windowService = windowService;
        Dashboard = dashboard;
        Monitoring = monitoring;
        CandidateDetails = candidateDetails;
        DuplicateCheck = duplicateCheck;
        BitrixIntegration = bitrixIntegration;
        Journal = journal;

        Monitoring.SelectedResponseChanged += async (_, response) =>
        {
            CandidateDetails.Update(response);
            DuplicateCheck.Update(response);
            await BitrixIntegration.UpdateAsync(response);
        };

        _monitoringService.StatusChanged += async (_, status) =>
        {
            SystemStatus = status;
            await RefreshAllAsync();
        };

        _monitoringService.StatusMessageChanged += (_, message) =>
        {
            SystemStatusDetails = message;
            IsMonitoringActive = _monitoringService.IsActive;
        };

        _monitoringService.ResponseProcessed += async (_, response) =>
        {
            await Monitoring.RefreshAsync();
            await Dashboard.RefreshAsync();
            await Journal.RefreshAsync();
            CandidateDetails.Update(response);
            DuplicateCheck.Update(response);
            await BitrixIntegration.UpdateAsync(response);
        };

    }

    public string SystemStatusText =>
        !IsMonitoringActive ? "Мониторинг не запущен" : SystemStatus switch
        {
            MonitoringStatus.Running => "Мониторинг работает",
            MonitoringStatus.Waiting => "Мониторинг ожидает следующий цикл",
            MonitoringStatus.RequiresAuthorization => "Нужна авторизация",
            MonitoringStatus.RequiresManualAction => "Нужно ручное действие",
            MonitoringStatus.Error => "Ошибка",
            MonitoringStatus.Stopped => "Мониторинг остановлен",
            _ => "Ожидание"
        };

    public bool IsMonitoringRunning => IsMonitoringActive;

    public string MonitoringActionText => IsMonitoringRunning ? "Остановить" : "Запустить мониторинг";

    public string MonitoringButtonText => IsMonitoringActive ? "Стоп" : "Старт";

    partial void OnSystemStatusChanged(MonitoringStatus value)
    {
        OnPropertyChanged(nameof(SystemStatusText));
    }

    partial void OnIsMonitoringActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMonitoringRunning));
        OnPropertyChanged(nameof(MonitoringActionText));
        OnPropertyChanged(nameof(MonitoringButtonText));
        OnPropertyChanged(nameof(SystemStatusText));
    }

    [RelayCommand]
    public Task StartMonitoringAsync() => _monitoringService.StartAsync(CancellationToken.None);

    [RelayCommand]
    public Task StopMonitoringAsync() => _monitoringService.StopAsync();

    [RelayCommand]
    public Task ToggleMonitoringAsync() =>
        IsMonitoringRunning
            ? _monitoringService.StopAsync()
            : _monitoringService.StartAsync(CancellationToken.None);

    [RelayCommand]
    public async Task OpenSettingsAsync(Window? owner)
    {
        if (owner is null)
        {
            return;
        }

        await _windowService.ShowSettingsAsync(owner, CancellationToken.None);
        await RefreshAllAsync();
    }

    [RelayCommand]
    public Task OpenMonitoringAsync(Window? owner) =>
        owner is null ? Task.CompletedTask : _windowService.ShowMonitoringAsync(owner, CancellationToken.None);

    [RelayCommand]
    public Task OpenJournalAsync(Window? owner) =>
        owner is null ? Task.CompletedTask : _windowService.ShowJournalAsync(owner, CancellationToken.None);

    public async Task InitializeAsync()
    {
        SystemStatus = _monitoringService.CurrentStatus;
        SystemStatusDetails = _monitoringService.CurrentStatusMessage;
        IsMonitoringActive = _monitoringService.IsActive;
        await RefreshAllAsync();
    }

    private async Task RefreshAllAsync()
    {
        await Dashboard.RefreshAsync();
        await Monitoring.RefreshAsync();
        await Journal.RefreshAsync();
        await BitrixIntegration.UpdateAsync(Monitoring.SelectedResponse);
        DuplicateCheck.Update(Monitoring.SelectedResponse);
        CandidateDetails.Update(Monitoring.SelectedResponse);
    }
}
