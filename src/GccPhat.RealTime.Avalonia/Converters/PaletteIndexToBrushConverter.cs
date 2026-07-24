using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GccPhat.RealTime.Converters;

/// <summary>Maps a <see cref="ViewModels.PairViewModel.PaletteIndex"/> to an Avalonia brush,
/// via <see cref="Palette"/>.</summary>
public sealed class PaletteIndexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        (byte r, byte g, byte b) = Palette.Get((int)value!);
        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
