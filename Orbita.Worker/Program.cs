using LeadFlow.Core.Data;
using LeadFlow.Core.Services.Worker;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
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

        if (TryHandleSilentInstall(args))
        {
            return;
        }

        var store = new WorkerConfigStore();
        var forceSetup = args.Any(arg => arg.Equals("--setup", StringComparison.OrdinalIgnoreCase));
        if (forceSetup)
        {
            if (!SetupWizardForm.TryConfigure(store, store.Load()))
            {
                return;
            }
        }
        else if (!EnsureConfigured(store))
        {
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

        var appSettings = new WorkerAppSettingsStore().LoadOrCreate();
        var host = Host.CreateApplicationBuilder();
        host.Services.AddSingleton(credentials);
        host.Services.AddSingleton(store);
        host.Services.AddSingleton(appSettings);
        host.Services.AddSingleton<WorkerRuntimeState>();
        host.Services.AddHttpClient(nameof(OrbitaApiClient));
        host.Services.AddSingleton<OrbitaApiClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var http = factory.CreateClient(nameof(OrbitaApiClient));
            return new OrbitaApiClient(http, sp.GetRequiredService<WorkerCredentials>());
        });
        host.Services.AddSingleton<OrbitaConfigProvider>();
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
        host.Services.AddSingleton<IAdsPowerAvitoAutomationService, AdsPowerAvitoAutomationService>();
        host.Services.AddSingleton<AvitoDemoResponseSource>();
        host.Services.AddSingleton<AvitoParserService>();
        host.Services.AddSingleton<IAvitoResponseSource, AvitoResponseSource>();
        host.Services.AddSingleton<WorkerAccountRuntimeStore>();
        host.Services.AddSingleton<EphemeralDedupCache>();
        host.Services.AddSingleton<WorkerCandidateOutbox>();
        host.Services.AddSingleton<OrbitaMonitoringRepository>();
        host.Services.AddSingleton<OrbitaCandidateDuplicateRepository>();
        host.Services.AddSingleton<IMonitoringRepository>(sp => sp.GetRequiredService<OrbitaMonitoringRepository>());
        host.Services.AddSingleton<ICandidateDuplicateRepository>(sp => sp.GetRequiredService<OrbitaCandidateDuplicateRepository>());
        host.Services.AddSingleton<WorkerActivityReporter>();
        host.Services.AddSingleton<IWorkerActivityReporter>(sp => sp.GetRequiredService<WorkerActivityReporter>());
        host.Services.AddSingleton<IWorkerMonitoringService, WorkerMonitoringService>();

        host.Services.AddSingleton<WorkerOrchestrator>();
        host.Services.AddHostedService(sp => sp.GetRequiredService<WorkerOrchestrator>());

        var builtHost = host.Build();
        builtHost.Start();

        var orchestrator = builtHost.Services.GetRequiredService<WorkerOrchestrator>();
        var runtimeState = builtHost.Services.GetRequiredService<WorkerRuntimeState>();

        var shutdownService = builtHost.Services.GetRequiredService<WorkerShutdownService>();
        var updateStore = builtHost.Services.GetRequiredService<WorkerUpdateStore>();

        Application.Run(new TrayApplicationContext(orchestrator, runtimeState, store, credentials));

        builtHost.StopAsync().GetAwaiter().GetResult();

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

                WorkerRestartHelper.LaunchInstallScript(installPath);
            }

            return;
        }

        if (shutdownService.PendingRestart)
        {
            WorkerRestartHelper.LaunchProcessRestart();
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
                return mutex;
            }

            mutex.Dispose();
            if (!updateRestart)
            {
                break;
            }

            Thread.Sleep(retryDelayMs);
        }

        MessageBox.Show(
            "Воркер Орбиты уже запущен.",
            WorkerSetupConstants.ProductName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return null;
    }

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