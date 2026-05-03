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

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        Environment.SetEnvironmentVariable("LOG_SERVICE_NAME", "LeadFlow");
        base.OnStartup(e);
        LogStartup("OnStartup entered");

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
        if (_host is not null)
        {
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private static void LogStartup(string message)
    {
        GlobalLogger.Instance.LogAsync(
            message,
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(OnStartup),
            filePath: "App.xaml.cs").GetAwaiter().GetResult();
    }
}
