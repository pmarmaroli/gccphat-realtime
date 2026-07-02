using System;
using System.Globalization;
using System.Windows.Data;

namespace GccPhat.RealTime.Converters
{
    /// <summary>
    /// Binds a frequency-in-Hz property to a linear <see cref="System.Windows.Controls.Slider"/> whose
    /// track position represents log10(Hz), so the slider reads logarithmically while the bound
    /// property stays in Hz (the single source of truth).
    /// </summary>
    public sealed class LogFrequencySliderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => Math.Log10((double)value);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Math.Pow(10.0, (double)value);
    }
}
