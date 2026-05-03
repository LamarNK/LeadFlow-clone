using System.Windows;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class AvitoAuthWindow : Window
{
    public AvitoAuthWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is AvitoAuthViewModel viewModel)
        {
            viewModel.StopMonitoring();
        }
    }
}
