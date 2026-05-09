using System.Windows;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class MonitoringWindow : Window
{
    public MonitoringWindow(MonitoringViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
