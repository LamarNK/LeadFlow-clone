using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class SettingsWindow : Window
{
    private bool _allowClose;

    public SettingsWindow()
    {
        InitializeComponent();
        Closing += OnClosingAsync;
        Closed += OnClosed;
    }

    private async void OnClosingAsync(object? sender, CancelEventArgs e)
    {
        if (_allowClose || DataContext is not SettingsViewModel viewModel || !viewModel.HasUnsavedChanges())
        {
            return;
        }

        e.Cancel = true;

        var result = MessageBox.Show(
            this,
            "Есть несохраненные изменения. Сохранить их перед закрытием?",
            "Несохраненные изменения",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
        {
            return;
        }

        if (result == MessageBoxResult.Yes)
        {
            await viewModel.SaveAsync();
        }

        _allowClose = true;
        _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Normal);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.DetachPersistenceListener();
            viewModel.DeleteCommittedProfiles();
        }
    }

    private async void AccountsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ToggleButton>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (DataContext is not SettingsViewModel viewModel || viewModel.SelectedAccount is null)
        {
            return;
        }

        e.Handled = true;
        await viewModel.OpenAvitoProfileAsync(this);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
