using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GccPhat.RealTime.Converters;

/// <summary>Maps a platform-neutral <see cref="ChannelActivity"/> to an Avalonia brush, using
/// <see cref="ChannelActivityColors"/> as the single source of truth.</summary>
public sealed class ChannelActivityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        (byte r, byte g, byte b) = ChannelActivityColors.Get((ChannelActivity)value!);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
