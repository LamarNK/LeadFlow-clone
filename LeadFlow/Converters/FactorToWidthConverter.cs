using System.Globalization;
using System.Windows.Data;

namespace LeadFlow.Converters;

public sealed class FactorToWidthConverter : IValueConverter
{
    public static readonly FactorToWidthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var factor = value is double d ? d : 0.0;
        var maxWidth = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 200.0;

        if (double.IsNaN(factor) || double.IsInfinity(factor)) return 0.0;
        return Math.Max(4.0, factor * maxWidth);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
