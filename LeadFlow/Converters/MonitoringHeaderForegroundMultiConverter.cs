using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;


namespace LeadFlow.Converters;

/// <summary>
/// Цвет заголовка статуса мониторинга: при остановленном мониторинге — предупреждающий янтарь,
/// при активном — по текущему <see cref="MonitoringStatus"/> (как в <see cref="StatusToBrushConverter"/>).
/// </summary>
public sealed class MonitoringHeaderForegroundMultiConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush WaitingBrush = Create("#16A34A");
    private static readonly SolidColorBrush RunningBrush = Create("#2563EB");
    private static readonly SolidColorBrush RecoveringBrush = Create("#D97706");
    private static readonly SolidColorBrush AuthBrush = Create("#F97316");
    private static readonly SolidColorBrush ManualBrush = Create("#F97316");
    private static readonly SolidColorBrush ErrorBrush = Create("#EF4444");
    private static readonly SolidColorBrush StoppedBrush = Create("#64748B");
    private static readonly SolidColorBrush InactiveBrush = Create("#D97706");

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var active = values.Length > 0 && values[0] is bool b && b;
        if (!active)
        {
            if (values.Length > 1 && values[1] is MonitoringStatus st && st == MonitoringStatus.Error)
            {
                return ErrorBrush;
            }

            return InactiveBrush;
        }

        return values.Length > 1 && values[1] is MonitoringStatus status
            ? status switch
            {
                MonitoringStatus.Waiting => WaitingBrush,
                MonitoringStatus.Running => RunningBrush,
                MonitoringStatus.Recovering => RecoveringBrush,
                MonitoringStatus.RequiresAuthorization => AuthBrush,
                MonitoringStatus.RequiresManualAction => ManualBrush,
                MonitoringStatus.Error => ErrorBrush,
                MonitoringStatus.Stopped => StoppedBrush,
                _ => StoppedBrush
            }
            : StoppedBrush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Create(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
