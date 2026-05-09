using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LeadFlow.Converters;

/// <summary>Преобразует строку вида #RRGGBB в <see cref="SolidColorBrush"/> (замороженный).</summary>
public sealed class HexColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s || s.Length < 4)
        {
            return Brushes.Transparent;
        }

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(s);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
