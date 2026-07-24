using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GccPhat.RealTime.Converters
{
    /// <summary>Maps a <see cref="PairViewModel.PaletteIndex"/> to the WPF brush the pair-list
    /// swatch and plot lines use, via <see cref="Palette"/>.</summary>
    public sealed class PaletteIndexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            (byte r, byte g, byte b) = Palette.Get((int)value);
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
