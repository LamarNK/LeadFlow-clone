using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.DependencyInjection;

namespace LeadFlow.ViewModels;

/// <summary>
/// Одно окно Avito с несколькими вкладками (отдельный WebView2 и сессия на вкладку).
/// </summary>
public partial class AvitoBrowserHostViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private Window? _windowHost;

    /// <summary>Отступ между вкладками из отрицательного Margin (совпадает с AvitoAuthWindow).</summary>
    private const double TabOverlapPx = 18;

    /// <summary>Запас справа: кнопки окна, стрелки прокрутки вкладок, поля.</summary>
    private const double TabStripRightReservePx = 230;

    private const double TabStripHorizontalMarginsPx = 52;

    public ObservableCollection<AvitoAuthViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private AvitoAuthViewModel? selectedTab;

    /// <summary>Максимальная ширина вкладки в полосе (уменьшается при большом числе вкладок).</summary>
    [ObservableProperty]
    private double tabChromeMaxWidth = 200;

    /// <summary>Максимальная ширина текста имени на вкладке.</summary>
    [ObservableProperty]
    private double tabLabelMaxWidth = 156;

    public string WindowTitle => Tabs.Count switch
    {
        0 => "Avito",
        1 => $"Avito — {Tabs[0].AccountName}",
        _ => $"Avito — вкладок: {Tabs.Count}",
    };

    public AvitoBrowserHostViewModel(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        Tabs.CollectionChanged += OnTabsCollectionChanged;
    }

    partial void OnSelectedTabChanged(AvitoAuthViewModel? value)
    {
        SyncActiveTabFlags();
        NotifyTabCommands();
    }

    public void AttachWindow(Window window)
    {
        if (_windowHost is not null)
        {
            _windowHost.StateChanged -= OnHostWindowStateChanged;
            _windowHost.SizeChanged -= OnWindowHostSizeChanged;
            _windowHost.Loaded -= OnWindowHostLoaded;
        }

        _windowHost = window;
        _windowHost.StateChanged += OnHostWindowStateChanged;
        _windowHost.SizeChanged += OnWindowHostSizeChanged;
        _windowHost.Loaded += OnWindowHostLoaded;
        OnPropertyChanged(nameof(IsWindowMaximized));
        ScheduleRefreshTabStripSizing();
    }

    private void OnWindowHostLoaded(object sender, RoutedEventArgs e) =>
        RefreshTabStripSizing();

    private void OnWindowHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        RefreshTabStripSizing();

    /// <summary>Состояние окна для иконки «Развернуть / Восстановить».</summary>
    public bool IsWindowMaximized => _windowHost?.WindowState == WindowState.Maximized;

    private void OnHostWindowStateChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(IsWindowMaximized));

    [RelayCommand]
    private void MinimizeHostWindow() =>
        ExecuteIfHost(static w => w.WindowState = WindowState.Minimized);

    [RelayCommand]
    private void ToggleMaximizeHostWindow() =>
        ExecuteIfHost(static w =>
            w.WindowState = w.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized);

    [RelayCommand]
    private void CloseHostWindow() => _windowHost?.Close();

    private void ExecuteIfHost(Action<Window> action)
    {
        if (_windowHost is null)
        {
            return;
        }

        action(_windowHost);
    }

    public void AddAuthTab(AvitoAccount account)
    {
        if (TryActivateExisting(account.Id, initialUrl: null, isAuth: true))
        {
            return;
        }

        var tab = CreateTabViewModel();
        tab.ConfigureForAuthorization(account);
        HookTab(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
        SyncActiveTabFlags();
    }

    public void AddProfileTab(AvitoAccount account, string? initialUrl)
    {
        if (TryActivateExisting(account.Id, initialUrl, isAuth: false))
        {
            return;
        }

        var tab = CreateTabViewModel();
        if (string.IsNullOrWhiteSpace(initialUrl))
        {
            tab.ConfigureForProfile(account);
        }
        else
        {
            tab.ConfigureForProfile(account, initialUrl);
        }

        HookTab(tab);
        Tabs.Add(tab);
        SelectedTab = tab;
        SyncActiveTabFlags();
    }

    private bool TryActivateExisting(Guid accountId, string? initialUrl, bool isAuth)
    {
        foreach (var t in Tabs)
        {
            if (t.AccountKey != accountId)
            {
                continue;
            }

            SelectedTab = t;
            SyncActiveTabFlags();
            if (!isAuth && !string.IsNullOrWhiteSpace(initialUrl))
            {
                t.ApplyExternalNavigate(initialUrl);
            }

            return true;
        }

        return false;
    }

    /// <summary>Закрывает все вкладки, относящиеся к аккаунту (освобождает WebView2).</summary>
    public void CloseTabsForAccount(Guid accountId)
    {
        foreach (var tab in Tabs.Where(t => t.AccountKey == accountId).ToList())
        {
            CloseTab(tab);
        }
    }

    [RelayCommand]
    private void CloseTab(AvitoAuthViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return;
        }

        UnhookTab(tab);
        tab.StopMonitoring();
        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        Application.Current?.Dispatcher.Invoke(
            tab.ReleaseBrowser,
            DispatcherPriority.ApplicationIdle);

        if (Tabs.Count == 0)
        {
            _windowHost?.Close();
            return;
        }

        if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs[Math.Min(idx, Tabs.Count - 1)];
        }
        else
        {
            SyncActiveTabFlags();
        }

        NotifyTabCommands();
    }

    public void OnWindowClosed()
    {
        if (_windowHost is not null)
        {
            _windowHost.StateChanged -= OnHostWindowStateChanged;
            _windowHost.SizeChanged -= OnWindowHostSizeChanged;
            _windowHost.Loaded -= OnWindowHostLoaded;
            _windowHost = null;
        }

        foreach (var t in Tabs.ToList())
        {
            UnhookTab(t);
            t.ReleaseBrowser();
        }

        Tabs.Clear();
    }

    private void HookTab(AvitoAuthViewModel tab) =>
        tab.PropertyChanged += OnTabPropertyChanged;

    private void UnhookTab(AvitoAuthViewModel tab) =>
        tab.PropertyChanged -= OnTabPropertyChanged;

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AvitoAuthViewModel.AccountName) && Tabs.Count == 1)
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    private AvitoAuthViewModel CreateTabViewModel() =>
        ActivatorUtilities.CreateInstance<AvitoAuthViewModel>(_serviceProvider);

    private void SyncActiveTabFlags()
    {
        foreach (var t in Tabs)
        {
            t.IsActiveTab = ReferenceEquals(t, SelectedTab);
        }
    }

    private void NotifyTabCommands()
    {
        if (SelectedTab is null)
        {
            return;
        }

        SelectedTab.NavigateBackCommand.NotifyCanExecuteChanged();
        SelectedTab.NavigateForwardCommand.NotifyCanExecuteChanged();
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(WindowTitle));
        ScheduleRefreshTabStripSizing();
    }

    private void ScheduleRefreshTabStripSizing()
    {
        if (_windowHost is null)
        {
            return;
        }

        _windowHost.Dispatcher.BeginInvoke(RefreshTabStripSizing, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Делит ширину полосы вкладок между вкладками с учётом наложения (<see cref="TabOverlapPx"/> между соседями).
    /// </summary>
    private void RefreshTabStripSizing()
    {
        if (_windowHost is null || Tabs.Count == 0)
        {
            return;
        }

        var w = _windowHost.ActualWidth;
        if (w <= 1)
        {
            return;
        }

        var usable = w - TabStripRightReservePx - TabStripHorizontalMarginsPx;
        if (usable < 120)
        {
            usable = 120;
        }

        var n = Tabs.Count;
        var perTab = (usable + TabOverlapPx * Math.Max(0, n - 1)) / n;
        var maxTab = Math.Clamp(perTab, 72.0, 220.0);
        var labelMax = Math.Max(28.0, maxTab - 44.0);

        if (Math.Abs(maxTab - TabChromeMaxWidth) > 0.25 || Math.Abs(labelMax - TabLabelMaxWidth) > 0.25)
        {
            TabChromeMaxWidth = maxTab;
            TabLabelMaxWidth = labelMax;
        }
    }
}
