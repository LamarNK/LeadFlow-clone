using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LeadFlow.ViewModels;

namespace LeadFlow.Views;

public partial class AvitoAuthWindow : Window
{
    public AvitoAuthWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    /// <summary>
    /// Кастомный Chrome + <c>IsHitTestVisibleInChrome</c> отключают обычный HTCAPTION;
    /// перетаскивание делаем явно, не забирая клики у кнопок, вкладок и полей ввода.
    /// </summary>
    private void ChromeRegion_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || ShouldSuppressWindowDrag(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Окно ещё не показано или кнопка уже отпущена — игнорируем.
        }
    }

    private static bool ShouldSuppressWindowDrag(DependencyObject? source)
    {
        for (var d = source; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            switch (d)
            {
                case Button:
                case ToggleButton:
                case TextBoxBase:
                case PasswordBox:
                case ListBoxItem:
                case ComboBox:
                case Slider:
                case ScrollBar:
                case Thumb:
                    return true;
            }
        }

        return false;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is AvitoBrowserHostViewModel host)
        {
            host.OnWindowClosed();
            return;
        }

        if (DataContext is AvitoAuthViewModel viewModel)
        {
            viewModel.StopMonitoring();
        }
    }
}
