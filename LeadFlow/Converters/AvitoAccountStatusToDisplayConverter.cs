using System.Globalization;
using System.Windows.Data;


namespace LeadFlow.Converters;

public sealed class AvitoAccountStatusToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is AvitoAccountStatus s ? ToDisplay(s) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static string ToDisplay(AvitoAccountStatus s) => s switch
    {
        AvitoAccountStatus.NotConfigured => "Не настроен",
        AvitoAccountStatus.RequiresLogin => "Нужно войти",
        AvitoAccountStatus.Authorized => "Готов к работе",
        AvitoAccountStatus.Monitoring => "Мониторится",
        AvitoAccountStatus.Paused => "Приостановлен",
        AvitoAccountStatus.RequiresManualAction => "Нужно действие",
        AvitoAccountStatus.Error => "Ошибка",
        _ => s.ToString()
    };
}
