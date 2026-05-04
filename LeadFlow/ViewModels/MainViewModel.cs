using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
using LeadFlow.Services;

namespace LeadFlow.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IMonitoringService _monitoringService;
    private readonly IWindowService _windowService;
    private bool _startNotificationShown;
    private bool _pauseNotificationShown;

    public event EventHandler<DesktopNotificationRequest>? NotificationRequested;

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
            try
            {
                await InvokeOnUiThreadAsync(async () =>
                {
                    CandidateDetails.Update(response);
                    DuplicateCheck.Update(response);
                    await BitrixIntegration.UpdateAsync(response);
                });
            }
            catch (Exception ex)
            {
                await LogUiHandlerFailureAsync("selected response change", ex);
            }
        };

        _monitoringService.StatusChanged += (_, status) =>
        {
            try
            {
                InvokeOnUiThread(() =>
                {
                    var wasActive = IsMonitoringActive;
                    SystemStatus = status;

                    if (status == MonitoringStatus.Running && _monitoringService.IsActive && !wasActive && !_startNotificationShown)
                    {
                        _startNotificationShown = true;
                        _pauseNotificationShown = false;
                        RaiseNotification(new DesktopNotificationRequest
                        {
                            Title = "Мониторинг запущен",
                            Message = "LeadFlow начал проверку аккаунтов и новых откликов.",
                            Severity = DesktopNotificationSeverity.Info
                        });
                    }

                    if (status == MonitoringStatus.Waiting && _monitoringService.IsActive && !_pauseNotificationShown)
                    {
                        _pauseNotificationShown = true;
                        RaiseNotification(new DesktopNotificationRequest
                        {
                            Title = "Мониторинг на паузе",
                            Message = string.IsNullOrWhiteSpace(_monitoringService.CurrentStatusMessage)
                                ? "Цикл завершён, ждём следующую проверку."
                                : _monitoringService.CurrentStatusMessage,
                            Severity = DesktopNotificationSeverity.Info
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                _ = LogUiHandlerFailureAsync("status change", ex);
            }
        };

        _monitoringService.StatusMessageChanged += (_, message) =>
        {
            try
            {
                InvokeOnUiThread(() =>
                {
                    SystemStatusDetails = message;
                    IsMonitoringActive = _monitoringService.IsActive;

                    if (!_monitoringService.IsActive)
                    {
                        _startNotificationShown = false;
                        _pauseNotificationShown = false;
                    }
                    else if (SystemStatus == MonitoringStatus.Running)
                    {
                        _pauseNotificationShown = false;
                    }
                });
            }
            catch (Exception ex)
            {
                _ = LogUiHandlerFailureAsync("status message change", ex);
            }
        };

        _monitoringService.ResponseProcessed += async (_, response) =>
        {
            try
            {
                await InvokeOnUiThreadAsync(async () =>
                {
                    Monitoring.ApplyProcessedResponse(response);
                    Dashboard.ApplyProcessedResponse(response);
                    Journal.ApplyProcessedResponse(response);
                    CandidateDetails.Update(response);
                    DuplicateCheck.Update(response);
                    await BitrixIntegration.UpdateAsync(response);
                    RaiseNotification(new DesktopNotificationRequest
                    {
                        Title = "Новый отклик",
                        Message = $"{response.FullName} — {response.Vacancy} ({response.AccountName})",
                        Severity = response.Status == ResponseStatus.Error ? DesktopNotificationSeverity.Warning : DesktopNotificationSeverity.Info
                    });
                });
            }
            catch (Exception ex)
            {
                await LogUiHandlerFailureAsync("processed response", ex);
            }
        };

        _monitoringService.ProfileStatsUpdated += (_, args) =>
        {
            try
            {
                InvokeOnUiThread(() =>
                {
                    if (!args.HasNewBlockedAds)
                    {
                        return;
                    }

                    var blockedText = args.Account.BlockedCount == 1
                        ? "1 объявление заблокировано"
                        : $"{args.Account.BlockedCount} объявлений заблокировано";

                    RaiseNotification(new DesktopNotificationRequest
                    {
                        Title = "Объявление заблокировано",
                        Message = $"{args.Account.DisplayName}: {blockedText}. Новых блокировок: +{args.BlockedCountDelta}.",
                        Severity = DesktopNotificationSeverity.Warning,
                        TimeoutMilliseconds = 7000
                    });
                });
            }
            catch (Exception ex)
            {
                _ = LogUiHandlerFailureAsync("profile stats update", ex);
            }
        };
    }

    public string SystemStatusText =>
        !IsMonitoringActive ? "Мониторинг не запущен" : SystemStatus switch
        {
            MonitoringStatus.Running => "Мониторинг работает",
            MonitoringStatus.Waiting => "Мониторинг ожидает следующий цикл",
            MonitoringStatus.Recovering => "Восстановление после сбоя цикла",
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

    private void RaiseNotification(DesktopNotificationRequest request)
    {
        NotificationRequested?.Invoke(this, request);
    }

    private static void InvokeOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private static Task InvokeOnUiThreadAsync(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return action();
        }

        return dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private static Task LogUiHandlerFailureAsync(string context, Exception exception) =>
        GlobalLogger.Instance.LogAsync(
            $"Unhandled UI event error during {context}.{Environment.NewLine}{exception}",
            DeskLinkAuditLogLevel.Error);
}