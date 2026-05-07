using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class AvitoAuthWindow : Window
{
    private ScrollViewer? _tabsScrollViewer;

    public AvitoAuthWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    private void AvitoAuthWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(HookTabsScrollViewer, DispatcherPriority.Loaded);
    }

    private void HookTabsScrollViewer()
    {
        if (_tabsScrollViewer is not null)
        {
            _tabsScrollViewer.ScrollChanged -= TabsScrollViewer_OnScrollChanged;
            _tabsScrollViewer = null;
        }

        _tabsScrollViewer = FindScrollViewer(TabsListBox);
        if (_tabsScrollViewer is not null)
        {
            _tabsScrollViewer.ScrollChanged += TabsScrollViewer_OnScrollChanged;
        }

        UpdateTabsScrollChromeState();
    }

    private void TabsScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e) =>
        UpdateTabsScrollChromeState();

    private void UpdateTabsScrollChromeState()
    {
        if (_tabsScrollViewer is null)
        {
            HookTabsScrollViewer();
        }

        if (_tabsScrollViewer is null)
        {
            TabsScrollLeftButton.IsEnabled = false;
            TabsScrollRightButton.IsEnabled = false;
            return;
        }

        var w = _tabsScrollViewer.ScrollableWidth;
        if (w <= 1)
        {
            TabsScrollLeftButton.IsEnabled = false;
            TabsScrollRightButton.IsEnabled = false;
            return;
        }

        var x = _tabsScrollViewer.HorizontalOffset;
        TabsScrollLeftButton.IsEnabled = x > 0.5;
        TabsScrollRightButton.IsEnabled = x < w - 0.5;
    }

    private void TabsListBox_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = _tabsScrollViewer ?? FindScrollViewer(TabsListBox);
        if (sv is null || sv.ScrollableWidth <= 1)
        {
            return;
        }

        const double step = 48;
        var next = sv.HorizontalOffset + (e.Delta > 0 ? -step : step);
        sv.ScrollToHorizontalOffset(Math.Clamp(next, 0, sv.ScrollableWidth));
        e.Handled = true;
        UpdateTabsScrollChromeState();
    }

    private void TabsScrollLeft_OnClick(object sender, RoutedEventArgs e)
    {
        var sv = _tabsScrollViewer ?? FindScrollViewer(TabsListBox);
        if (sv is null || sv.ScrollableWidth <= 1)
        {
            return;
        }

        sv.ScrollToHorizontalOffset(Math.Max(0, sv.HorizontalOffset - 120));
        UpdateTabsScrollChromeState();
    }

    private void TabsScrollRight_OnClick(object sender, RoutedEventArgs e)
    {
        var sv = _tabsScrollViewer ?? FindScrollViewer(TabsListBox);
        if (sv is null || sv.ScrollableWidth <= 1)
        {
            return;
        }

        sv.ScrollToHorizontalOffset(Math.Min(sv.ScrollableWidth, sv.HorizontalOffset + 120));
        UpdateTabsScrollChromeState();
    }

    private void TabsListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is null)
        {
            return;
        }

        lb.Dispatcher.BeginInvoke(
            () =>
            {
                lb.ScrollIntoView(lb.SelectedItem);
                UpdateTabsScrollChromeState();
            },
            DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? obj)
    {
        if (obj is null)
        {
            return null;
        }

        if (obj is ScrollViewer sv)
        {
            return sv;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            var found = FindScrollViewer(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Кастомный Chrome + <c>IsHitTestVisibleInChrome</c> отключают обычный HTCAPTION;
    /// перетаскивание делаем явно, не забирая клики у кнопок, вкладок и полей ввода.
    /// </summary>
    private void ChromeRegion_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || ShouldSuppressWindowDrag(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Окно ещё не показано или кнопка уже отпущена — игнорируем.
        }
    }

    private static bool ShouldSuppressWindowDrag(DependencyObject? source)
    {
        for (var d = source; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            switch (d)
            {
                case Button:
                case ToggleButton:
                case TextBoxBase:
                case PasswordBox:
                case ListBoxItem:
                case ComboBox:
                case Slider:
                case ScrollBar:
                case Thumb:
                    return true;
            }
        }

        return false;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_tabsScrollViewer is not null)
        {
            _tabsScrollViewer.ScrollChanged -= TabsScrollViewer_OnScrollChanged;
            _tabsScrollViewer = null;
        }

        if (DataContext is AvitoBrowserHostViewModel host)
        {
            host.OnWindowClosed();
            return;
        }

        if (DataContext is AvitoAuthViewModel viewModel)
        {
            viewModel.StopMonitoring();
        }
    }
}
