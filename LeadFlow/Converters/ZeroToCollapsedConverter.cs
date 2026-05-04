using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LeadFlow.Converters
{
    public class ZeroToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue)
            {
                var isZero = intValue == 0;
                var invert = parameter?.ToString() == "Invert";
                
                // Если invert=true: показываем когда 0, скрываем когда >0
                // Если invert=false (по умолчанию): скрываем когда 0, показываем когда >0
                return (isZero && invert) || (!isZero && !invert) ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
