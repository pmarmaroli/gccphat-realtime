using System;
using System.Windows.Media;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime.ViewModels;

/// <summary>Live level meter for one capture channel, used by the "identify" step.</summary>
public sealed class ChannelMeterViewModel : ObservableObject
{
    private static readonly Brush IdleBrush = Freeze(Color.FromRgb(120, 144, 156));
    private static readonly Brush ActiveBrush = Freeze(Color.FromRgb(44, 160, 44));

    private double _levelDb = double.NegativeInfinity;
    private double _barValue;
    private bool _isActive;

    public ChannelMeterViewModel(int index)
    {
        Index = index;
    }

    public int Index { get; }
    public string Name => $"Ch {Index}";

    /// <summary>Updates the meter from a linear RMS level (≈ 0..1 full scale).</summary>
    public void SetLevel(double linear)
    {
        double db = linear <= 1e-7 ? double.NegativeInfinity : 20.0 * Math.Log10(linear);
        _levelDb = db;

        // Map -60..0 dBFS onto 0..100 for the bar.
        double frac = double.IsNegativeInfinity(db) ? 0.0 : (db + 60.0) / 60.0;
        BarValue = Math.Clamp(frac, 0.0, 1.0) * 100.0;
        OnPropertyChanged(nameof(LevelText));
    }

    public double LevelDb => _levelDb;

    public double BarValue
    {
        get => _barValue;
        private set => SetProperty(ref _barValue, value);
    }

    public string LevelText => double.IsNegativeInfinity(_levelDb) ? "  -inf" : $"{_levelDb,6:F1}dB";

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(BarBrush));
            }
        }
    }

    public Brush BarBrush => _isActive ? ActiveBrush : IdleBrush;

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
