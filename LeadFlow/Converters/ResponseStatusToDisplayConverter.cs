using System.Globalization;
using System.Windows.Data;
using LeadFlow.Models;

namespace LeadFlow.Converters;

public sealed class ResponseStatusToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ResponseStatus status)
        {
            return ResponseStatusFormatting.ShortLabel(status);
        }

        if (value is string s && Enum.TryParse<ResponseStatus>(s, out var parsed))
        {
            return ResponseStatusFormatting.ShortLabel(parsed);
        }

        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
