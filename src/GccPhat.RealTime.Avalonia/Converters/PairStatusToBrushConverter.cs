using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GccPhat.RealTime.Converters;

/// <summary>Maps a platform-neutral <see cref="PairStatus"/> to an Avalonia brush, using
/// <see cref="PairStatusColors"/> as the single source of truth.</summary>
public sealed class PairStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        (byte r, byte g, byte b) = PairStatusColors.Get((PairStatus)value!);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
