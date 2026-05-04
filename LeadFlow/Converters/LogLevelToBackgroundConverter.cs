using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LeadFlow.Converters;

public sealed class LogLevelToBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value?.ToString()?.ToLowerInvariant();
        return level switch
        {
            "error" or "critical" => (Brush)Application.Current.FindResource("ErrorBrush"),
            "warning" => (Brush)Application.Current.FindResource("WarningBrush"),
            "information" or "info" => (Brush)Application.Current.FindResource("PrimaryBrush"),
            "debug" or "trace" => System.Windows.Media.Brushes.Gray,
            _ => System.Windows.Media.Brushes.SlateGray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
