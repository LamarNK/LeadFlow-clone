using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using LeadFlow.Models;

namespace LeadFlow.Converters;

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value?.ToString() ?? string.Empty;
        return key switch
        {
            nameof(ResponseStatus.Sent) or nameof(AvitoAccountStatus.Authorized) or nameof(MonitoringStatus.Waiting) => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A")),
            nameof(ResponseStatus.Duplicate) or nameof(AvitoAccountStatus.RequiresManualAction) => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F97316")),
            nameof(ResponseStatus.Error) or nameof(AvitoAccountStatus.Error) or nameof(MonitoringStatus.Error) => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")),
            nameof(MonitoringStatus.Running) or nameof(AvitoAccountStatus.Monitoring) => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB")),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
