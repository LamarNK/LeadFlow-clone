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

    public string SystemStatusText => SystemStatus switch
    {
        MonitoringStatus.Running => "Мониторинг запущен",
        MonitoringStatus.RequiresAuthorization => "Требуется авторизация",
        MonitoringStatus.RequiresManualAction => "Требуется ручное действие",
        MonitoringStatus.Error => "Ошибка",
        MonitoringStatus.Stopped => "Мониторинг остановлен",
        _ => "Ожидание"
    };

    partial void OnSystemStatusChanged(MonitoringStatus value) => OnPropertyChanged(nameof(SystemStatusText));

    [RelayCommand]
    public Task StartMonitoringAsync() => _monitoringService.StartAsync(CancellationToken.None);

    [RelayCommand]
    public Task StopMonitoringAsync() => _monitoringService.StopAsync();

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
    public Task OpenCandidateDetailsAsync(Window? owner) =>
        owner is null ? Task.CompletedTask : _windowService.ShowCandidateDetailsAsync(owner, CancellationToken.None);

    [RelayCommand]
    public Task OpenDuplicateCheckAsync(Window? owner) =>
        owner is null ? Task.CompletedTask : _windowService.ShowDuplicateCheckAsync(owner, CancellationToken.None);

    [RelayCommand]
    public Task OpenBitrixIntegrationAsync(Window? owner) =>
        owner is null ? Task.CompletedTask : _windowService.ShowBitrixIntegrationAsync(owner, CancellationToken.None);

    [RelayCommand]
    public Task OpenJournalAsync(Window? owner) =>
        owner is null ? Task.CompletedTask : _windowService.ShowJournalAsync(owner, CancellationToken.None);

    public Task InitializeAsync() => RefreshAllAsync();

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
