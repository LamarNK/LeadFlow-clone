using System.Windows;
using LeadFlow.Data;
using LeadFlow.Logging.Audit;
using LeadFlow.Services;
using LeadFlow.Services.Avito;
using LeadFlow.Services.Bitrix;
using LeadFlow.Services.Browser;
using LeadFlow.ViewModels;
using LeadFlow.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LeadFlow;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;

    private const string SingleInstanceMutexName = @"Local\LeadFlow.SingleInstance";
    private const string SingleInstanceActivationEventName = @"Local\LeadFlow.SingleInstance.Activate";

    protected override void OnStartup(StartupEventArgs e)
    {
        var createdNew = false;
        RegisterGlobalExceptionHandlers();

        try
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            _activationEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: SingleInstanceActivationEventName);
        }
        catch
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            _activationEvent?.Dispose();
            _activationEvent = null;
        }

        if (_singleInstanceMutex is not null && !createdNew)
        {
            _activationEvent?.Set();
            Shutdown();
            return;
        }

        Environment.SetEnvironmentVariable("LOG_SERVICE_NAME", "LeadFlow");
        base.OnStartup(e);
        LogStartup("OnStartup entered");

        if (_activationEvent is not null)
        {
            _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                static (state, _) => ((App)state!).ActivateExistingInstance(),
                this,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        var settingsService = new JsonSettingsService();
        var settings = settingsService.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        LogStartup("Settings loaded");

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<ISettingsService>(settingsService);
        builder.Services.AddSingleton<IBrowserProfileService, BrowserProfileService>();
        builder.Services.AddSingleton<IBrowserSessionService, BrowserSessionService>();
        builder.Services.AddSingleton<IWebPageAutomationService, WebView2PageAutomationService>();
        builder.Services.AddSingleton<IAvitoPageReaderService, AvitoPageReaderService>();
        builder.Services.AddSingleton<IPhoneNormalizer, PhoneNormalizer>();
        builder.Services.AddSingleton<ICandidateParser, CandidateParser>();
        builder.Services.AddSingleton<IDuplicateService, DuplicateService>();
        builder.Services.AddSingleton<IBitrixClient, BitrixClient>();
        builder.Services.AddSingleton<ICsvExportService, CsvExportService>();
        builder.Services.AddSingleton<IAvitoResponseSource, AvitoResponseSource>();
        builder.Services.AddSingleton<AvitoDemoResponseSource>();
        builder.Services.AddSingleton<LeadFlow.Services.Avito.AvitoParserService>();
        builder.Services.AddSingleton<AppRepository>();
        builder.Services.AddSingleton<IWindowService, WindowService>();
        builder.Services.AddSingleton<IMonitoringService, MonitoringService>();

        builder.Services.AddDbContextFactory<AppDbContext>((_, options) =>
        {
            options.UseSqlite($"Data Source={settings.DatabasePath}");
        });

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<DashboardViewModel>();
        builder.Services.AddSingleton<MonitoringViewModel>();
        builder.Services.AddSingleton<CandidateDetailsViewModel>();
        builder.Services.AddSingleton<DuplicateCheckViewModel>();
        builder.Services.AddSingleton<BitrixIntegrationViewModel>();
        builder.Services.AddSingleton<JournalViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<AvitoAuthViewModel>();

        builder.Services.AddTransient<MainWindow>();
        builder.Services.AddTransient<MonitoringWindow>();
        builder.Services.AddTransient<CandidateDetailsWindow>();
        builder.Services.AddTransient<DuplicateCheckWindow>();
        builder.Services.AddTransient<BitrixIntegrationWindow>();
        builder.Services.AddTransient<JournalWindow>();
        builder.Services.AddTransient<SettingsWindow>();
        builder.Services.AddTransient<AvitoAuthWindow>();

        _host = builder.Build();
        LogStartup("Host built");

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        LogStartup("MainWindow resolved");
        var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
        LogStartup("MainViewModel resolved");
        mainWindow.DataContext = mainViewModel;
        MainWindow = mainWindow;
        mainWindow.Show();
        LogStartup("MainWindow shown");

        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                LogStartup("Async initialization started");
                using var scope = _host.Services.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<AppRepository>();
                await repository.InitializeAsync(settings, CancellationToken.None);
                LogStartup("Repository initialized");
                await repository.AddLogAsync(new Models.ProcessingLogItem
                {
                    AccountId = Guid.Empty,
                    CreatedAt = DateTime.UtcNow,
                    Level = "Info",
                    Message = "Приложение запущено",
                    Details = "LeadFlow initialized."
                }, CancellationToken.None);
                await mainViewModel.InitializeAsync();
                LogStartup("MainViewModel initialized");
                GlobalLogger.Instance.LogAsync("LeadFlow started.", DeskLinkAuditLogLevel.Info).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogStartup($"Initialization failed: {ex}");
                MessageBox.Show(
                    $"Не удалось инициализировать LeadFlow.{Environment.NewLine}{ex}",
                    "LeadFlow",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        });
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private void ActivateExistingInstance()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var window = MainWindow;
            if (window is null)
            {
                return;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        });
    }

    private static void LogStartup(string message)
    {
        GlobalLogger.Instance.LogAsync(
            message,
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(OnStartup),
            filePath: "App.xaml.cs").GetAwaiter().GetResult();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            GlobalLogger.Instance.LogAsync(
                $"DispatcherUnhandledException.{Environment.NewLine}{args.Exception}",
                DeskLinkAuditLogLevel.Error,
                memberName: nameof(RegisterGlobalExceptionHandlers),
                filePath: "App.xaml.cs").GetAwaiter().GetResult();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exceptionText = args.ExceptionObject is Exception ex
                ? ex.ToString()
                : args.ExceptionObject?.ToString() ?? "Unknown exception object";

            GlobalLogger.Instance.LogAsync(
                $"AppDomain.UnhandledException (IsTerminating={args.IsTerminating}).{Environment.NewLine}{exceptionText}",
                DeskLinkAuditLogLevel.Error,
                memberName: nameof(RegisterGlobalExceptionHandlers),
                filePath: "App.xaml.cs").GetAwaiter().GetResult();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            GlobalLogger.Instance.LogAsync(
                $"TaskScheduler.UnobservedTaskException.{Environment.NewLine}{args.Exception}",
                DeskLinkAuditLogLevel.Error,
                memberName: nameof(RegisterGlobalExceptionHandlers),
                filePath: "App.xaml.cs").GetAwaiter().GetResult();
        };
    }
}
