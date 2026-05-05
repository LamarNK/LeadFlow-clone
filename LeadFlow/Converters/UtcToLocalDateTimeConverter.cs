using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LeadFlow.Converters;

/// <summary>Для привязок: значение из хранилища (UTC / Unspecified как UTC) → локальное время ПК для <see cref="Binding.StringFormat"/>.</summary>
public sealed class UtcToLocalDateTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return null;
        }

        return value is DateTime dt
            ? dt.ToLocalTimeFromStoredUtc()
            : DependencyProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
