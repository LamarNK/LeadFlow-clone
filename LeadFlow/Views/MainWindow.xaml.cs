using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using WinForms = System.Windows.Forms;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class MainWindow : Window
{
    private WinForms.NotifyIcon? _trayIcon;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        InitializeTrayIcon();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.PropertyChanged += ViewModel_PropertyChanged;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsMonitoringRunning))
        {
            UpdateTrayIconTooltip();
        }
    }

    private void MainWindow_Closing(object sender, CancelEventArgs e)
    {
        if (_isClosing) return;

        if (DataContext is MainViewModel vm && vm.IsMonitoringRunning)
        {
            e.Cancel = true;

            var result = MessageBox.Show(
                "Мониторинг активен. Завершить работу приложения?",
                "Подтверждение",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                _isClosing = true;
                vm.ToggleMonitoringCommand?.Execute(null);
                
                Task.Delay(200).ContinueWith(_ =>
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        _isClosing = false;
                        Close();
                    });
                }, TaskScheduler.Default);
            }
        }
    }

    private void InitializeTrayIcon()
    {
        // Безопасное получение иконки из exe-файла приложения (гарантирует валидный .ico формат)
        var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = appIcon,
            Text = "LeadFlow — мониторинг откликов",
            Visible = false
        };

        _trayIcon.ContextMenuStrip = new WinForms.ContextMenuStrip();
        _trayIcon.ContextMenuStrip.Items.Add("Открыть", null, (s, e) => ShowWindow());
        _trayIcon.ContextMenuStrip.Items.Add("Свернуть", null, (s, e) => HideToTray());
        _trayIcon.ContextMenuStrip.Items.Add(new WinForms.ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add("Выход", null, (s, e) => System.Windows.Application.Current.Shutdown());

        _trayIcon.MouseDoubleClick += (s, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                ShowWindow();
        };

        UpdateTrayIconTooltip();
    }

    private void UpdateTrayIconTooltip()
    {
        if (_trayIcon != null && DataContext is MainViewModel vm)
        {
            var status = vm.IsMonitoringRunning ? "● Активен" : "○ Остановлен";
            _trayIcon.Text = $"LeadFlow — {status}";
        }
    }

    private void MainWindow_StateChanged(object sender, System.EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _trayIcon != null)
        {
            HideToTray();
        }
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon != null) _trayIcon.Visible = false;
    }

    private void HideToTray()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = true;
            Hide();
        }
    }

    protected override void OnClosed(System.EventArgs e)
    {
        base.OnClosed(e);
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
