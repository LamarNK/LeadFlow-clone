using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace LeadFlow.Attached;

public static class DatePickerChrome
{
    public static readonly DependencyProperty SuppressInnerWatermarkProperty =
        DependencyProperty.RegisterAttached(
            "SuppressInnerWatermark",
            typeof(bool),
            typeof(DatePickerChrome),
            new PropertyMetadata(false, OnSuppressInnerWatermarkChanged));

    public static bool GetSuppressInnerWatermark(DependencyObject obj) =>
        (bool)obj.GetValue(SuppressInnerWatermarkProperty);

    public static void SetSuppressInnerWatermark(DependencyObject obj, bool value) =>
        obj.SetValue(SuppressInnerWatermarkProperty, value);

    private static void OnSuppressInnerWatermarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DatePicker picker)
            return;

        if (e.NewValue is true)
        {
            picker.Loaded -= OnPickerLoaded;
            picker.Loaded += OnPickerLoaded;
            ScheduleClear(picker);
        }
        else
        {
            picker.Loaded -= OnPickerLoaded;
        }
    }

    private static void OnPickerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DatePicker picker)
            ScheduleClear(picker);
    }

    private static void ScheduleClear(DatePicker picker)
    {
        picker.Dispatcher.BeginInvoke(new Action(() => ClearInnerWatermark(picker)), DispatcherPriority.ApplicationIdle);
    }

    private static void ClearInnerWatermark(DatePicker picker)
    {
        picker.ApplyTemplate();
        if (picker.Template?.FindName("PART_TextBox", picker) is not DatePickerTextBox tb)
            return;

        tb.ApplyTemplate();
        if (tb.Template?.FindName("PART_Watermark", tb) is UIElement fromTemplate)
        {
            fromTemplate.Visibility = Visibility.Collapsed;
            return;
        }

        CollapseDescendantNamed(tb, "PART_Watermark");
    }

    private static void CollapseDescendantNamed(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name)
            {
                fe.Visibility = Visibility.Collapsed;
                return;
            }

            CollapseDescendantNamed(child, name);
        }
    }
}
