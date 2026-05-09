using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class AccountSettingsWindow : Window
{
    public AccountSettingsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AccountSettingsViewModel viewModel)
        {
            viewModel.LoadCommand.Execute(null);
        }
    }

    private void UaPresetPopup_OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is AccountSettingsViewModel vm)
        {
            vm.IsUaPresetDropdownOpen = false;
        }
    }

    private void RegenerateUserAgent_OnClick(object sender, RoutedEventArgs e)
    {
        BindingOperations.GetBindingExpression(UaAssignedTextBox, TextBox.TextProperty)?.UpdateSource();
        if (DataContext is AccountSettingsViewModel vm)
        {
            vm.RegenerateUserAgent();
        }
    }
}
