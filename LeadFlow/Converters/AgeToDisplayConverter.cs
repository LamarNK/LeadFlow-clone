using System.Globalization;
using System.Windows.Data;

namespace LeadFlow.Converters;

/// <summary>Показ возраста: «25 лет» или «—», если нет данных.</summary>
public sealed class AgeToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int age)
        {
            return $"{age} лет";
        }

        return "—";
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
