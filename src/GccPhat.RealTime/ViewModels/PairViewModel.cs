using System;
using System.Windows.Media;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime.ViewModels;

/// <summary>An active channel pair shown in the list, with its latest live readout.</summary>
public sealed class PairViewModel : ObservableObject
{
    private const double SignalFloorDb = -60.0;

    private static readonly Brush GoodBrush = Freeze(Color.FromRgb(44, 160, 44));
    private static readonly Brush WeakBrush = Freeze(Color.FromRgb(214, 154, 39));
    private static readonly Brush PoorBrush = Freeze(Color.FromRgb(170, 170, 170));

    private double _delayMs;
    private double _coherence;
    private double _levelADb = double.NegativeInfinity;
    private double _levelBDb = double.NegativeInfinity;
    private bool _valid;

    public PairViewModel(ChannelPair pair, int paletteIndex)
    {
        Pair = pair;
        PaletteIndex = paletteIndex;
        (byte r, byte g, byte b) = Palette.Get(paletteIndex);
        ColorBrush = Freeze(Color.FromRgb(r, g, b));
    }

    public ChannelPair Pair { get; }
    public int PaletteIndex { get; }
    public Brush ColorBrush { get; }
    public string Label => Pair.ToString();

    /// <summary>Updates all live readouts from the latest result (called on the UI thread).</summary>
    public void SetLive(in PairResult result)
    {
        _valid = result.Valid;
        if (result.Valid)
        {
            _delayMs = result.DelayMs;
            _coherence = result.Coherence;
            _levelADb = ToDb(result.LevelA);
            _levelBDb = ToDb(result.LevelB);
        }

        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(CoherenceText));
        OnPropertyChanged(nameof(LevelAText));
        OnPropertyChanged(nameof(LevelBText));
        OnPropertyChanged(nameof(QualityBrush));
        OnPropertyChanged(nameof(SignalText));
    }

    /// <summary>Clears the live readouts (e.g. when stopped).</summary>
    public void ClearLive()
    {
        _valid = false;
        SetLive(new PairResult(Pair, 0, 0, 0, 0, 0, 0, Valid: false));
    }

    public string DelayText => _valid ? $"{_delayMs,8:F3} ms" : "    --   ms";
    public string CoherenceText => _valid ? $"sim {_coherence:F2}" : "sim  -- ";
    public string LevelAText => _valid ? $"A {FormatDb(_levelADb)}" : "A  -- ";
    public string LevelBText => _valid ? $"B {FormatDb(_levelBDb)}" : "B  -- ";

    /// <summary>Short health note: flags channels with no signal, otherwise blank.</summary>
    public string SignalText
    {
        get
        {
            if (!_valid)
            {
                return string.Empty;
            }
            bool aSilent = _levelADb < SignalFloorDb;
            bool bSilent = _levelBDb < SignalFloorDb;
            if (aSilent && bSilent) return "no signal";
            if (aSilent) return "no signal on A";
            if (bSilent) return "no signal on B";
            return string.Empty;
        }
    }

    /// <summary>Green/amber/grey dot reflecting how reliable the delay estimate is.</summary>
    public Brush QualityBrush
    {
        get
        {
            if (!_valid || _levelADb < SignalFloorDb || _levelBDb < SignalFloorDb)
            {
                return PoorBrush;
            }
            if (_coherence >= 0.6) return GoodBrush;
            if (_coherence >= 0.3) return WeakBrush;
            return PoorBrush;
        }
    }

    private static double ToDb(double linear)
        => linear <= 1e-7 ? double.NegativeInfinity : 20.0 * Math.Log10(linear);

    private static string FormatDb(double db)
        => double.IsNegativeInfinity(db) ? "  -inf" : $"{db,6:F1}dB";

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
