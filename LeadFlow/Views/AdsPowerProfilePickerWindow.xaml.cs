using System.Windows;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class AdsPowerProfilePickerWindow : Window
{
    public AdsPowerProfilePickerWindow(AdsPowerProfilePickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    public AdsPowerPickerResult? Result { get; private set; }

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AdsPowerProfilePickerViewModel vm)
        {
            return;
        }

        if (!vm.TryBuildResult(out var result) || result is null)
        {
            return;
        }

        Result = result;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
