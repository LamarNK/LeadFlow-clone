namespace LeadFlow.Views;

public partial class StatisticsHistoryView
{
    public StatisticsHistoryView()
    {
        InitializeComponent();
    }

    private void TrendHost_OnSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (DataContext is LeadFlow.ViewModels.StatisticsHistoryViewModel vm)
        {
            vm.UpdateTrendViewportWidth(Math.Max(0d, e.NewSize.Width - 4d));
        }
    }
}
