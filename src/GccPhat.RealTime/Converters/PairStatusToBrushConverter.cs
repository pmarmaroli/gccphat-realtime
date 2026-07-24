using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GccPhat.RealTime.Converters
{
    /// <summary>Maps a platform-neutral <see cref="PairStatus"/> to the WPF brush the UI used to
    /// bind directly, using <see cref="PairStatusColors"/> as the single source of truth.</summary>
    public sealed class PairStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            (byte r, byte g, byte b) = PairStatusColors.Get((PairStatus)value);
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
