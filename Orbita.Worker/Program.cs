using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Worker;
using Microsoft.EntityFrameworkCore;
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

        using var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Воркер Орбиты уже запущен.",
                WorkerSetupConstants.ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
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

        var appSettings = CreateAppSettings();
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
        host.Services.AddSingleton<IWorkerConfigProvider>(sp => sp.GetRequiredService<OrbitaConfigProvider>());
        host.Services.AddSingleton<SystemMetricsCollector>();
        host.Services.AddHttpClient(nameof(WorkerSystemInfoCollector));
        host.Services.AddSingleton<WorkerSystemInfoCollector>();
        host.Services.AddSingleton<WorkerUpdateStore>();
        host.Services.AddHostedService<WorkerUpdateCoordinator>();

        host.Services.AddSingleton<IPhoneNormalizer, PhoneNormalizer>();
        host.Services.AddSingleton<ICandidateParser, CandidateParser>();
        host.Services.AddSingleton<IAdsPowerApiClient, AdsPowerApiClient>();
        host.Services.AddSingleton<IAdsPowerAvitoAutomationService, AdsPowerAvitoAutomationService>();
        host.Services.AddSingleton<AvitoDemoResponseSource>();
        host.Services.AddSingleton<AvitoParserService>();
        host.Services.AddSingleton<IAvitoResponseSource, AvitoResponseSource>();
        host.Services.AddSingleton<AppRepository>();
        host.Services.AddSingleton<IMonitoringRepository>(sp => sp.GetRequiredService<AppRepository>());
        host.Services.AddSingleton<ICandidateDuplicateRepository>(sp => sp.GetRequiredService<AppRepository>());
        host.Services.AddSingleton<IWorkerMonitoringService, WorkerMonitoringService>();

        host.Services.AddDbContextFactory<AppDbContext>((sp, options) =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            options.UseSqlite(EncryptedSqliteConnectionBuilder.BuildConnectionString(settings));
        });

        host.Services.AddSingleton<WorkerOrchestrator>();
        host.Services.AddHostedService(sp => sp.GetRequiredService<WorkerOrchestrator>());

        var builtHost = host.Build();
        builtHost.Start();

        var orchestrator = builtHost.Services.GetRequiredService<WorkerOrchestrator>();
        var runtimeState = builtHost.Services.GetRequiredService<WorkerRuntimeState>();

        Application.Run(new TrayApplicationContext(orchestrator, runtimeState, store, credentials));

        builtHost.StopAsync().GetAwaiter().GetResult();
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

    private static AppSettings CreateAppSettings()
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrbitaWorker",
            "Data");

        Directory.CreateDirectory(dataDir);

        return new AppSettings
        {
            DatabasePath = Path.Combine(dataDir, "worker.db"),
            DatabaseEncryptionKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            DuplicateScope = DuplicateScope.GlobalAcrossAllAccounts,
            MonitoringSafety = new MonitoringSafetyOptions { MaxConcurrentAccounts = 1 }
        };
    }
}