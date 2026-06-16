using System.Windows;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class BalanceDetailsWindow : Window
{
    public BalanceDetailsWindow(BalanceDetailsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
