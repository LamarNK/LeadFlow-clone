using LeadFlow.Core.Data;
using LeadFlow.Core.Services.Worker;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.LocalChrome;
using LeadFlow.Core.Services.Multilogin;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Captcha;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orbita.Worker.Services;
using SQLitePCL;

namespace Orbita.Worker;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\Orbita.Worker.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        Environment.SetEnvironmentVariable("LOG_SERVICE_NAME", "Orbita.Worker");
        ApplicationConfiguration.Initialize();

        WorkerLifecycleLog.InfoAsync(
            $"Worker lifecycle: запуск процесса (аргументы: {FormatArgs(args)})",
            nameof(Main),
            new Dictionary<string, object?> { ["worker.args"] = FormatArgs(args) })
            .GetAwaiter().GetResult();

        if (TryHandleSilentInstall(args))
        {
            WorkerLifecycleLog.InfoAsync(
                "Worker lifecycle: тихая установка API-ключа завершена, выход",
                nameof(Main))
                .GetAwaiter().GetResult();
            return;
        }

        var store = new WorkerConfigStore();
        var forceSetup = args.Any(arg => arg.Equals("--setup", StringComparison.OrdinalIgnoreCase));
        if (forceSetup)
        {
            if (!SetupWizardForm.TryConfigure(store, store.Load()))
            {
                WorkerLifecycleLog.InfoAsync(
                    "Worker lifecycle: мастер настройки отменён, выход",
                    nameof(Main))
                    .GetAwaiter().GetResult();
                return;
            }
        }
        else if (!EnsureConfigured(store))
        {
            WorkerLifecycleLog.InfoAsync(
                "Worker lifecycle: воркер не настроен, выход",
                nameof(Main))
                .GetAwaiter().GetResult();
            return;
        }

        var updateRestart = args.Any(arg =>
            arg.Equals(WorkerRestartHelper.UpdateRestartArgument, StringComparison.OrdinalIgnoreCase));
        using var mutex = AcquireSingleInstanceMutex(updateRestart);
        if (mutex is null)
        {
            return;
        }

        RunTrayApplication(store);

        WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: процесс завершён",
            nameof(Main))
            .GetAwaiter().GetResult();
    }

    internal static bool EnsureConfigured(WorkerConfigStore store)
    {
        var credentials = store.Load();
        if (WorkerConfigStore.IsConfigured(credentials))
        {
            return true;
        }

        return SetupWizardForm.TryConfigure(store, credentials);
    }

    internal static void RunTrayApplication(WorkerConfigStore store)
    {
        Batteries_V2.Init();

        var credentials = store.Load();
        if (!WorkerConfigStore.IsConfigured(credentials))
        {
            return;
        }

        var host = Host.CreateApplicationBuilder();
        host.Services.AddSingleton(credentials);
        host.Services.AddSingleton(store);
        host.Services.AddSingleton<WorkerRuntimeState>();
        host.Services.AddHttpClient(nameof(OrbitaApiClient));
        host.Services.AddHttpClient(nameof(AvitoAvatarDownloader), client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri("https://www.avito.ru/");
        });
        host.Services.AddSingleton<OrbitaApiClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient(nameof(OrbitaApiClient));
            return new OrbitaApiClient(http, sp.GetRequiredService<WorkerCredentials>());
        });
        host.Services.AddSingleton<OrbitaConfigProvider>();
        host.Services.AddSingleton<AvitoAvatarDownloader>();
        host.Services.AddSingleton<OrbitaCandidateSink>();
        host.Services.AddSingleton<INewCandidateSink>(sp => sp.GetRequiredService<OrbitaCandidateSink>());
        host.Services.AddSingleton<WorkerEventSink>();
        host.Services.AddSingleton<IWorkerEventSink>(sp => sp.GetRequiredService<WorkerEventSink>());
        host.Services.AddSingleton<WorkerTelemetryCollector>();
        host.Services.AddSingleton<OrbitaTelemetrySink>();
        host.Services.AddSingleton<IWorkerTelemetrySink>(sp => sp.GetRequiredService<OrbitaTelemetrySink>());
        host.Services.AddSingleton<DiagnosticsUploadService>();
        host.Services.AddSingleton<IWorkerDiagnosticsUploader>(sp => sp.GetRequiredService<DiagnosticsUploadService>());
        host.Services.AddSingleton<WorkerLogSyncState>();
        host.Services.AddSingleton<WorkerLogUploadService>();
        host.Services.AddSingleton<IWorkerLogsUploader>(sp => sp.GetRequiredService<WorkerLogUploadService>());
        host.Services.AddHostedService<WorkerLogSyncService>();
        host.Services.AddHostedService<WorkerLogCleanupService>();
        host.Services.AddHostedService<EphemeralCacheCleanupService>();
        host.Services.AddHostedService<WorkerOutboxRetryService>();
        host.Services.AddSingleton<IWorkerConfigProvider>(sp => sp.GetRequiredService<OrbitaConfigProvider>());
        host.Services.AddSingleton<SystemMetricsCollector>();
        host.Services.AddHttpClient(nameof(WorkerSystemInfoCollector));
        host.Services.AddSingleton<WorkerSystemInfoCollector>();
        host.Services.AddSingleton<WorkerUpdateStore>();
        host.Services.AddSingleton<WorkerUpdateGate>();
        host.Services.AddSingleton<WorkerUpdateOfferSource>();
        host.Services.AddSingleton<WorkerShutdownService>();
        host.Services.AddSingleton<IWorkerPendingUpdateCoordinator, WorkerPendingUpdateCoordinator>();
        host.Services.AddHostedService<WorkerUpdateCoordinator>();

        host.Services.AddSingleton<IPhoneNormalizer, PhoneNormalizer>();
        host.Services.AddSingleton<ICandidateParser, CandidateParser>();
        host.Services.AddSingleton<IAdsPowerApiClient, AdsPowerApiClient>();
        host.Services.AddHttpClient<IRuCaptchaClient, RuCaptchaClient>(client =>
        {
            client.BaseAddress = new Uri(RuCaptchaClient.DefaultBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        host.Services.AddSingleton<IAvitoGeeTestSolver, AvitoGeeTestSolver>();
        host.Services.AddSingleton<IAdsPowerAvitoAutomationService, AdsPowerAvitoAutomationService>();
        host.Services.AddSingleton<IMultiloginApiClient, MultiloginApiClient>();
        host.Services.AddSingleton<IMultiloginBrowserConnector, PuppeteerMultiloginBrowserConnector>();
        host.Services.AddSingleton<IMultiloginCdpConnector, MultiloginCdpConnector>();
        host.Services.AddSingleton<ILocalChromeBrowserLauncher, LocalChromeBrowserLauncher>();
        host.Services.AddSingleton<LocalChromeAccountLock>();
        host.Services.AddSingleton<WorkerAccountSessionFactory>();
        host.Services.AddSingleton<LeadFlow.Core.Services.Captcha.CaptchaSessionHost>();
        host.Services.AddSingleton<CaptchaSessionCoordinator>();
        host.Services.AddSingleton<BrowserMonitorSource>();
        host.Services.AddSingleton<IBrowserMonitorSource>(sp => sp.GetRequiredService<BrowserMonitorSource>());
        host.Services.AddSingleton<BrowserMonitorCoordinator>();
        host.Services.AddSingleton<LocalChromeLoginCoordinator>();
        host.Services.AddSingleton<TopUpSessionCoordinator>();
        host.Services.AddSingleton<AvitoDemoResponseSource>();
        host.Services.AddSingleton<AvitoParserService>();
        host.Services.AddSingleton<IAvitoResponseSource, AvitoResponseSource>();
        host.Services.AddSingleton<WorkerAccountRuntimeStore>();
        host.Services.AddSingleton<EphemeralDedupCache>();
        host.Services.AddSingleton<ResponsePhoneObservationStore>();
        host.Services.AddSingleton<IResponsePhoneObservationStore>(sp => sp.GetRequiredService<ResponsePhoneObservationStore>());
        host.Services.AddSingleton<WorkerCandidateOutbox>();
        host.Services.AddSingleton<OrbitaMonitoringRepository>();
        host.Services.AddSingleton<OrbitaCandidateDuplicateRepository>();
        host.Services.AddSingleton<IMonitoringRepository>(sp => sp.GetRequiredService<OrbitaMonitoringRepository>());
        host.Services.AddSingleton<ICandidateDuplicateRepository>(sp => sp.GetRequiredService<OrbitaCandidateDuplicateRepository>());
        host.Services.AddSingleton<IOutboundChatDispatch, OrbitaOutboundChatDispatch>();
        host.Services.AddSingleton<WorkerActivityReporter>();
        host.Services.AddSingleton<IWorkerActivityReporter>(sp => sp.GetRequiredService<WorkerActivityReporter>());
        host.Services.AddSingleton<IWorkerTopUpHistoryConfirmation, OrbitaTopUpHistoryConfirmation>();
        host.Services.AddSingleton<MonitoringCycleJournalSink>();
        host.Services.AddSingleton<IMonitoringCycleJournal>(sp => sp.GetRequiredService<MonitoringCycleJournalSink>());
        host.Services.AddSingleton<IWorkerMonitoringService, WorkerMonitoringService>();
        host.Services.AddSingleton<OrbitaAvitoAdListingCatalog>();
        host.Services.AddSingleton<LeadFlow.Core.Services.Avito.IAvitoAdListingCatalog>(
            sp => sp.GetRequiredService<OrbitaAvitoAdListingCatalog>());
        host.Services.AddSingleton<WorkerAvitoAdsMonitor>();
        host.Services.AddHostedService<WorkerAvitoAdsMonitorHost>();

        host.Services.AddSingleton<WorkerHubConnection>();
        host.Services.AddSingleton<IWorkerRealtimeChannel>(sp => sp.GetRequiredService<WorkerHubConnection>());
        host.Services.AddHostedService(sp => sp.GetRequiredService<WorkerHubConnection>());
        host.Services.AddSingleton<WorkerOrchestrator>();
        host.Services.AddHostedService(sp => sp.GetRequiredService<WorkerOrchestrator>());

        var builtHost = host.Build();
        builtHost.Start();

        var orchestrator = builtHost.Services.GetRequiredService<WorkerOrchestrator>();
        var runtimeState = builtHost.Services.GetRequiredService<WorkerRuntimeState>();

        var shutdownService = builtHost.Services.GetRequiredService<WorkerShutdownService>();
        var updateStore = builtHost.Services.GetRequiredService<WorkerUpdateStore>();

        WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: хост запущен, открытие трея",
            nameof(RunTrayApplication))
            .GetAwaiter().GetResult();

        Application.Run(new TrayApplicationContext(
            orchestrator,
            runtimeState,
            store,
            credentials,
            builtHost.Services.GetRequiredService<IHostApplicationLifetime>()));

        WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: трей закрыт, остановка хоста",
            nameof(RunTrayApplication),
            new Dictionary<string, object?>
            {
                ["shutdown.pendingRestart"] = shutdownService.PendingRestart,
                ["shutdown.pendingInstall"] = shutdownService.PendingInstallPath is not null,
                ["shutdown.restartScriptLaunched"] = shutdownService.RestartScriptLaunched,
                ["shutdown.installScriptLaunched"] = shutdownService.InstallScriptLaunched
            })
            .GetAwaiter().GetResult();

        builtHost.StopAsync().GetAwaiter().GetResult();

        WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: хост остановлен",
            nameof(RunTrayApplication))
            .GetAwaiter().GetResult();

        if (shutdownService.PendingInstallPath is { } installPath)
        {
            if (!shutdownService.InstallScriptLaunched)
            {
                var pendingMsi = updateStore.TryGetPendingMsi();
                if (pendingMsi is not null)
                {
                    updateStore.SavePendingInstall(pendingMsi.Version);
                    updateStore.ClearPendingMsi();
                }

                WorkerLifecycleLog.WarningAsync(
                    "Worker lifecycle: fallback запуск установщика обновления",
                    nameof(RunTrayApplication),
                    new Dictionary<string, object?> { ["update.msiPath"] = installPath })
                    .GetAwaiter().GetResult();
                WorkerRestartHelper.LaunchInstallScript(installPath);
            }

            return;
        }

        if (shutdownService.PendingRestart && !shutdownService.RestartScriptLaunched)
        {
            WorkerLifecycleLog.WarningAsync(
                "Worker lifecycle: fallback запуск скрипта перезапуска",
                nameof(RunTrayApplication))
                .GetAwaiter().GetResult();
            WorkerRestartHelper.LaunchProcessRestart(updateRestart: true);
        }
    }

    private static Mutex? AcquireSingleInstanceMutex(bool updateRestart)
    {
        const int retryCount = 120;
        const int retryDelayMs = 500;

        for (var attempt = 0; attempt < (updateRestart ? retryCount : 1); attempt++)
        {
            var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
            if (createdNew)
            {
                WorkerLifecycleLog.InfoAsync(
                    updateRestart
                        ? $"Worker lifecycle: mutex получен после перезапуска (попытка {attempt + 1})"
                        : "Worker lifecycle: mutex получен, единственный экземпляр",
                    nameof(AcquireSingleInstanceMutex),
                    new Dictionary<string, object?>
                    {
                        ["mutex.updateRestart"] = updateRestart,
                        ["mutex.attempt"] = attempt + 1
                    })
                    .GetAwaiter().GetResult();
                return mutex;
            }

            mutex.Dispose();
            if (!updateRestart)
            {
                break;
            }

            Thread.Sleep(retryDelayMs);
        }

        WorkerLifecycleLog.WarningAsync(
            updateRestart
                ? "Worker lifecycle: не удалось получить mutex после перезапуска, выход"
                : "Worker lifecycle: другой экземпляр уже запущен, выход",
            nameof(AcquireSingleInstanceMutex),
            new Dictionary<string, object?> { ["mutex.updateRestart"] = updateRestart })
            .GetAwaiter().GetResult();

        MessageBox.Show(
            "Воркер Орбиты уже запущен.",
            WorkerSetupConstants.ProductName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return null;
    }

    private static string FormatArgs(string[] args) =>
        args.Length == 0 ? "(нет)" : string.Join(' ', args.Select(arg => $"\"{arg}\""));

    private static bool TryHandleSilentInstall(string[] args)
    {
        if (!args.Contains("--install", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        string? apiKey = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--api-key", StringComparison.OrdinalIgnoreCase))
            {
                apiKey = args[i + 1];
            }
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        var store = new WorkerConfigStore();
        store.Save(WorkerConfigStore.CreateCredentials(apiKey));
        return true;
    }

}
