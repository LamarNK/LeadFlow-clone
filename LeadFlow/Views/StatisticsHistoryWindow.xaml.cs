using System.Windows;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class StatisticsHistoryWindow : Window
{
    public StatisticsHistoryWindow(StatisticsHistoryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RefreshCommand.Execute(null);
    }
}
