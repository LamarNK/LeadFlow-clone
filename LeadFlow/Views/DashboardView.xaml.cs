using System.Windows;
using System.Windows.Controls;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            NotifyChartWidth();
            NotifyAdsGridColumns();
        };
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
}
