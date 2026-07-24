using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GccPhat.RealTime.Converters
{
    /// <summary>Maps a platform-neutral <see cref="ChannelActivity"/> to the WPF brush the UI used
    /// to bind directly, using <see cref="ChannelActivityColors"/> as the single source of truth.</summary>
    public sealed class ChannelActivityToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            (byte r, byte g, byte b) = ChannelActivityColors.Get((ChannelActivity)value);
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
