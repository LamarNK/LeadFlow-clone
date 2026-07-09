using LeadFlow.Core.Logging.Audit;
using Orbita.Worker.Services;

namespace Orbita.Worker;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly WorkerOrchestrator _orchestrator;
    private readonly WorkerRuntimeState _runtimeState;
    private readonly WorkerConfigStore _configStore;
    private readonly WorkerCredentials _credentials;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleMonitoringItem;

    public TrayApplicationContext(
        WorkerOrchestrator orchestrator,
        WorkerRuntimeState runtimeState,
        WorkerConfigStore configStore,
        WorkerCredentials credentials)
    {
        _orchestrator = orchestrator;
        _runtimeState = runtimeState;
        _configStore = configStore;
        _credentials = credentials;

        _statusItem = new ToolStripMenuItem("Статус: подключение") { Enabled = false };
        _toggleMonitoringItem = new ToolStripMenuItem("Остановить мониторинг", null, OnToggleMonitoring);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_toggleMonitoringItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Настройки подключения", null, OnOpenSettings));
        menu.Items.Add(new ToolStripMenuItem("Открыть папку логов", null, OnOpenLogs));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, OnExit));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "OrbitaWorker.ico");
        Icon? icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;

        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "Орбита · воркер",
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += OnTrayDoubleClick;

        _runtimeState.Changed += (_, _) => UpdateUi();
        Application.ApplicationExit += OnApplicationExit;
        UpdateUi();

        _ = WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: трей открыт",
            nameof(TrayApplicationContext),
            new Dictionary<string, object?>
            {
                ["worker.displayName"] = _credentials.DisplayName ?? Environment.MachineName,
                ["worker.id"] = _credentials.WorkerId
            });
    }

    private void OnApplicationExit(object? sender, EventArgs e)
    {
        _ = WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: Application.ApplicationExit",
            nameof(OnApplicationExit));
        _trayIcon.Visible = false;
        ExitThread();
    }

    private void UpdateUi()
    {
        var detail = _runtimeState.Detail;
        _statusItem.Text = string.IsNullOrWhiteSpace(detail)
            ? $"Статус: {_runtimeState.Status}"
            : $"Статус: {_runtimeState.Status} — {detail}";
        _toggleMonitoringItem.Text = _runtimeState.IsMonitoring
            ? "Остановить мониторинг"
            : "Запустить мониторинг";
        _trayIcon.Text = TruncateTooltip(_runtimeState.Tooltip);
    }

    private void OnToggleMonitoring(object? sender, EventArgs e)
    {
        if (_runtimeState.IsMonitoring)
        {
            _orchestrator.RequestStopMonitoring();
        }
        else
        {
            _orchestrator.RequestStartMonitoring();
        }
    }

    private void OnTrayDoubleClick(object? sender, EventArgs e)
    {
        _trayIcon.ShowBalloonTip(
            3000,
            "Орбита · воркер",
            _runtimeState.Tooltip,
            ToolTipIcon.Info);
    }

    private void OnOpenSettings(object? sender, EventArgs e)
    {
        if (!SetupWizardForm.TryConfigure(_configStore, _credentials))
        {
            return;
        }

        var updated = _configStore.Load();
        _credentials.ApiBaseUrl = updated.ApiBaseUrl;
        _credentials.ApiKey = updated.ApiKey;
        _credentials.WorkerId = updated.WorkerId;
        _credentials.DisplayName = updated.DisplayName;

        MessageBox.Show(
            "Настройки сохранены. Перезапустите воркер, чтобы применить новый API-ключ.",
            WorkerSetupConstants.ProductName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void OnOpenLogs(object? sender, EventArgs e)
    {
        var dir = GlobalLogger.ResolveLogDirectoryForService("Orbita.Worker");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _ = WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: выход из трея по запросу пользователя",
            nameof(OnExit));
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        ExitThread();
    }

    private static string TruncateTooltip(string text) =>
        text.Length <= 127 ? text : text[..124] + "...";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}