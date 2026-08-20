using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace XCENA_Terminal_Dev.Controls
{
    /// <summary>
    /// #rrggbb text to a brush. Colours are stored as strings because that is what the terminal
    /// page consumes; the swatch in a list needs a brush, so the conversion happens in the binding.
    /// </summary>
    public sealed class HexBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string text && TryParse(text, out Windows.UI.Color color))
            {
                return new SolidColorBrush(color);
            }

            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();

        private static bool TryParse(string text, out Windows.UI.Color color)
        {
            color = Microsoft.UI.Colors.Transparent;

            string hex = text.TrimStart('#');
            if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint value))
            {
                return false;
            }

            color = Windows.UI.Color.FromArgb(
                0xFF,
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF));

            return true;
        }
    }
}
