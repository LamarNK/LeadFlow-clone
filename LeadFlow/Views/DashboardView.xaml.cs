using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class DashboardView : UserControl
{
    private Window? _hostWindowForAdsScroll;

    public DashboardView()
    {
        InitializeComponent();
        Loaded += DashboardView_Loaded;
        Unloaded += DashboardView_Unloaded;
        DataContextChanged += (_, _) =>
        {
            NotifyChartWidth();
            NotifyAdsGridColumns();
        };
    }

    private void DashboardView_Loaded(object sender, RoutedEventArgs e)
    {
        _hostWindowForAdsScroll = Window.GetWindow(this);
        if (_hostWindowForAdsScroll is not null)
        {
            _hostWindowForAdsScroll.SizeChanged += HostWindow_SizeChangedForAdsListMaxHeight;
        }

        UpdateAdsListScrollMaxHeight();
    }

    private void DashboardView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindowForAdsScroll is not null)
        {
            _hostWindowForAdsScroll.SizeChanged -= HostWindow_SizeChangedForAdsListMaxHeight;
        }

        _hostWindowForAdsScroll = null;
    }

    private void HostWindow_SizeChangedForAdsListMaxHeight(object sender, SizeChangedEventArgs e) =>
        UpdateAdsListScrollMaxHeight();

    /// <summary>
    /// При вложенности в внешний ScrollViewer (MainWindow) внутренний список иначе измеряется «на всю высоту контента»
    /// и теряет ScrollableHeight. Ограничиваем высоту области списка по окну.
    /// </summary>
    private void UpdateAdsListScrollMaxHeight()
    {
        var w = _hostWindowForAdsScroll ?? Window.GetWindow(this);
        if (w is null || w.ActualHeight < 80 || AdsListScrollViewer is null)
        {
            return;
        }

        // Шапка окна, блок CRM, заголовок/плитки/фильтры объявлений и отступы.
        const double verticalReserve = 320;
        AdsListScrollViewer.MaxHeight = Math.Max(200, w.ActualHeight - verticalReserve);
    }

    private void ChartActivityHost_OnLoaded(object sender, RoutedEventArgs e) =>
        NotifyChartWidth();

    private void ChartActivityHost_OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        NotifyChartWidth();

    private void AdsSectionHost_OnLoaded(object sender, RoutedEventArgs e) =>
        NotifyAdsGridColumns();

    private void AdsSectionHost_OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        NotifyAdsGridColumns();

    private void NotifyChartWidth()
    {
        if (DataContext is not DashboardViewModel vm)
        {
            return;
        }

        var w = ChartActivityHost.ActualWidth;
        if (w <= 0)
        {
            return;
        }

        vm.OnChartHostWidthChanged(w);
    }

    private void NotifyAdsGridColumns()
    {
        if (DataContext is not DashboardViewModel vm)
        {
            return;
        }

        var w = AdsSectionHost.ActualWidth;
        if (w <= 0)
        {
            return;
        }

        vm.OnAdsSectionWidthChanged(w);
    }

    /// <summary>
    /// Колесо над карточками (Button) и вложенный ScrollViewer внутри внешнего ScrollViewer главного окна.
    /// </summary>
    private void AdsListScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer inner)
        {
            return;
        }

        var outer = FindAncestorScrollViewer(inner);

        if (inner.ScrollableHeight >= 0.5)
        {
            if (CanScrollVertically(inner, e.Delta))
            {
                ApplyMouseWheelScroll(inner, e);
                e.Handled = true;
                return;
            }

            // Список объявлений у края — пробрасываем на страницу главного окна.
            if (outer is not null
                && outer.ScrollableHeight >= 0.5
                && CanScrollVertically(outer, e.Delta))
            {
                ApplyMouseWheelScroll(outer, e);
                e.Handled = true;
            }

            return;
        }

        // Внутренний список не прокручивается — отдаём колесо родительскому ScrollViewer (страница главного окна),
        // иначе встроенная логика вложенного ScrollViewer часто помечает событие обработанным без сдвига.
        if (outer is not null
            && outer.ScrollableHeight >= 0.5
            && CanScrollVertically(outer, e.Delta))
        {
            ApplyMouseWheelScroll(outer, e);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Delta &gt; 0 — колесо «от себя» (вверх по содержимому), &lt; 0 — «на себя» (вниз).
    /// </summary>
    private static bool CanScrollVertically(ScrollViewer sv, int delta)
    {
        const double eps = 0.51;
        if (sv.ScrollableHeight < 0.5)
        {
            return false;
        }

        if (delta > 0)
        {
            return sv.VerticalOffset > eps;
        }

        if (delta < 0)
        {
            return sv.VerticalOffset < sv.ScrollableHeight - eps;
        }

        return false;
    }

    private static void ApplyMouseWheelScroll(ScrollViewer scrollViewer, MouseWheelEventArgs e)
    {
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0)
        {
            if (e.Delta > 0)
            {
                scrollViewer.PageUp();
            }
            else
            {
                scrollViewer.PageDown();
            }
        }
        else
        {
            for (var i = 0; i < lines; i++)
            {
                if (e.Delta > 0)
                {
                    scrollViewer.LineUp();
                }
                else
                {
                    scrollViewer.LineDown();
                }
            }
        }
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject start)
    {
        for (var p = VisualTreeHelper.GetParent(start); p != null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is ScrollViewer sv)
            {
                return sv;
            }
        }

        return null;
    }
}
