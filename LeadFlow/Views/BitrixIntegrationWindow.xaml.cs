using System.Windows;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class BitrixIntegrationWindow : Window
{
    public BitrixIntegrationWindow(BitrixIntegrationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
