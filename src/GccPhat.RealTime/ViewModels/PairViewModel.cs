using System;
using System.Windows.Media;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime.ViewModels;

/// <summary>An active channel pair shown in the list, with its latest live readout.</summary>
public sealed class PairViewModel : ObservableObject
{
    private const double SignalFloorDb = -60.0;

    // Thresholds for the identical-vs-distinct verdict.
    private const double NearIdenticalDiff = 0.01;     // RMS(A-B)/RMS(A) < 1%  (~ -40 dB)
    private const double NearIdenticalCorr = 0.999;    // zero-lag correlation

    private static readonly Brush GoodBrush = Freeze(Color.FromRgb(44, 160, 44));
    private static readonly Brush WeakBrush = Freeze(Color.FromRgb(214, 154, 39));
    private static readonly Brush PoorBrush = Freeze(Color.FromRgb(170, 170, 170));
    private static readonly Brush MonoBrush = Freeze(Color.FromRgb(192, 57, 43));

    private double _delayMs;
    private double _coherence;
    private double _zeroLag;
    private double _diffRatio;
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
            _zeroLag = result.ZeroLagCorrelation;
            _diffRatio = result.DifferenceRatio;
            _levelADb = ToDb(result.LevelA);
            _levelBDb = ToDb(result.LevelB);
        }

        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(VerdictBrush));
        OnPropertyChanged(nameof(CorrText));
        OnPropertyChanged(nameof(DiffText));
        OnPropertyChanged(nameof(LevelAText));
        OnPropertyChanged(nameof(LevelBText));
        OnPropertyChanged(nameof(QualityBrush));
        OnPropertyChanged(nameof(SignalText));
    }

    /// <summary>Clears the live readouts (e.g. when stopped).</summary>
    public void ClearLive() => SetLive(new PairResult(Pair, 0, 0, 0, 0, 0, 0, 0, 0, Valid: false));

    public string DelayText => _valid ? $"{_delayMs,8:F3} ms" : "    --   ms";
    public string CorrText => _valid ? $"r0 {_zeroLag:F4}" : "r0  -- ";
    public string DiffText => _valid ? $"diff {FormatDb(DiffDb)}" : "diff  -- ";
    public string LevelAText => _valid ? $"A {FormatDb(_levelADb)}" : "A  -- ";
    public string LevelBText => _valid ? $"B {FormatDb(_levelBDb)}" : "B  -- ";

    private double DiffDb => _diffRatio <= 1e-7 ? double.NegativeInfinity : 20.0 * Math.Log10(_diffRatio);

    /// <summary>Verdict: are the two channels the same signal (mono) or genuinely distinct?</summary>
    public string VerdictText
    {
        get
        {
            if (!_valid)
            {
                return string.Empty;
            }
            if (_diffRatio <= 1e-12)
            {
                return "IDENTICAL (mono)";
            }
            if (_diffRatio < NearIdenticalDiff || _zeroLag > NearIdenticalCorr)
            {
                return "near-identical";
            }
            return "distinct (true dual)";
        }
    }

    public Brush VerdictBrush
    {
        get
        {
            if (!_valid)
            {
                return PoorBrush;
            }
            if (_diffRatio <= 1e-12)
            {
                return MonoBrush;
            }
            if (_diffRatio < NearIdenticalDiff || _zeroLag > NearIdenticalCorr)
            {
                return WeakBrush;
            }
            return GoodBrush;
        }
    }

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
