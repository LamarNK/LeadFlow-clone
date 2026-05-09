using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Linq;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class SettingsWindow : Window
{
    private bool _allowClose;
    private bool _isClosingSaveInProgress;

    public SettingsWindow()
    {
        InitializeComponent();
        Closing += OnClosingAsync;
        Closed += OnClosed;
    }

    private void AddAccountMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.IsAddMenuOpen = true;
        }
    }

    private async void OnClosingAsync(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosingSaveInProgress)
        {
            return;
        }

        _isClosingSaveInProgress = true;
        try
        {
            await viewModel.SaveAsync();
        }
        finally
        {
            _isClosingSaveInProgress = false;
        }

        _allowClose = true;
        _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Normal);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.DetachPersistenceListener();
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

        if (!viewModel.OpenAvitoProfileCommand.CanExecute(this))
        {
            return;
        }

        e.Handled = true;
        await viewModel.OpenAvitoProfileAsync(this);
    }

    private void SetAccountsDropHighlight(bool show)
    {
        if (AccountsListDropCard is null)
        {
            return;
        }

        if (show)
        {
            AccountsListDropCard.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xFC));
        }
        else
        {
            AccountsListDropCard.ClearValue(Border.BackgroundProperty);
        }
    }

    private void SettingsWindow_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        UpdateAccountsDropHighlightFromDrag(e);
    }

    private void SettingsWindow_OnDragLeave(object sender, DragEventArgs e)
    {
        SetAccountsDropHighlight(false);
    }

    private void SettingsWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SetAccountsDropHighlight(false);
        }
    }

    /// <summary>
    /// Подсветка по геометрии карточки на каждом <see cref="DragEventArgs"/> окна (tunneling),
    /// без <c>PreviewDragLeave</c> на Border/ListBox — там «выход» ложно срабатывает при переходе между родителем и дочерним ListBox.
    /// </summary>
    private void UpdateAccountsDropHighlightFromDrag(DragEventArgs e)
    {
        if (AccountsListDropCard is null || !AccountsListDropCard.IsVisible)
        {
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            SetAccountsDropHighlight(false);
            return;
        }

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        var validZip = paths?.Any(static p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) == true;

        var w = AccountsListDropCard.ActualWidth;
        var h = AccountsListDropCard.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var pt = e.GetPosition(AccountsListDropCard);
        var inside = pt.X >= 0 && pt.Y >= 0 && pt.X <= w && pt.Y <= h;
        SetAccountsDropHighlight(inside && validZip);
    }

    private void AccountsListDropCard_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var valid = e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            && paths.Any(static p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void AccountsListDropCard_OnDrop(object sender, DragEventArgs e)
    {
        SetAccountsDropHighlight(false);
        e.Handled = true;
        if (DataContext is not SettingsViewModel vm || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var w = Window.GetWindow((DependencyObject)sender);
        await vm.ImportProfileArchiveFromDroppedPathsAsync(paths, w);
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
