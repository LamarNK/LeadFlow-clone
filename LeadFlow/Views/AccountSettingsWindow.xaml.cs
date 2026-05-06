using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
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

    private void RegenerateUserAgent_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Пока фокус ещё в TextBox — явно отправляем текст в VM до смены фокуса и клика,
        // иначе привязка может позже перезаписать новый User-Agent старым значением из поля.
        var expr = BindingOperations.GetBindingExpression(UaAssignedTextBox, System.Windows.Controls.TextBox.TextProperty);
        expr?.UpdateSource();
    }

    private void RegenerateUserAgent_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountSettingsViewModel vm)
        {
            return;
        }

        // После LostFocus/обновления привязки — иначе иногда коммит TextBox идёт после Click и откатывает строку.
        Dispatcher.BeginInvoke(
            () =>
            {
                if (DataContext is AccountSettingsViewModel still)
                {
                    still.RegenerateUserAgent();
                }
            },
            DispatcherPriority.ApplicationIdle);
    }
}
