using System;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime.ViewModels;

public enum LocalizationPairState
{
    Used,
    Waiting,
    Ignored
}

/// <summary>An active channel pair shown in the list, with its latest live readout.</summary>
public sealed class PairViewModel : ObservableObject
{
    private const double SignalFloorDb = -60.0;

    // Thresholds for the identical-vs-distinct verdict.
    private const double NearIdenticalDiff = 0.01;     // RMS(A-B)/RMS(A) < 1%  (~ -40 dB)
    private const double NearIdenticalCorr = 0.999;    // zero-lag correlation

    private double _delayMs;
    private double _coherence;
    private double _zeroLag;
    private double _diffRatio;
    private double _levelADb = double.NegativeInfinity;
    private double _levelBDb = double.NegativeInfinity;
    private bool _valid;
    private LocalizationPairState _localizationState = LocalizationPairState.Waiting;
    private string _localizationStatusText = "Waiting for localization setup.";

    public PairViewModel(ChannelPair pair, int paletteIndex)
    {
        Pair = pair;
        PaletteIndex = paletteIndex;
    }

    public ChannelPair Pair { get; }
    public int PaletteIndex { get; }
    public string Label => Pair.ToString();
    public string LocalizationBadgeText => _localizationState switch
    {
        LocalizationPairState.Used => "USED",
        LocalizationPairState.Waiting => "WAITING",
        _ => "IGNORED"
    };

    public PairStatus LocalizationStatus => _localizationState switch
    {
        LocalizationPairState.Used => PairStatus.Good,
        LocalizationPairState.Waiting => PairStatus.Weak,
        _ => PairStatus.Mono
    };

    public string LocalizationStatusText => _localizationStatusText;
    public LocalizationPairState LocalizationState => _localizationState;

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
        OnPropertyChanged(nameof(VerdictStatus));
        OnPropertyChanged(nameof(CorrText));
        OnPropertyChanged(nameof(DiffText));
        OnPropertyChanged(nameof(LevelAText));
        OnPropertyChanged(nameof(LevelBText));
        OnPropertyChanged(nameof(QualityStatus));
        OnPropertyChanged(nameof(SignalText));
    }

    /// <summary>Clears the live readouts (e.g. when stopped).</summary>
    public void ClearLive() => SetLive(new PairResult(Pair, 0, 0, 0, 0, 0, 0, 0, 0, Valid: false));

    public void SetLocalizationState(LocalizationPairState state, string statusText)
    {
        if (_localizationState != state)
        {
            _localizationState = state;
            OnPropertyChanged(nameof(LocalizationBadgeText));
            OnPropertyChanged(nameof(LocalizationStatus));
        }

        if (_localizationStatusText != statusText)
        {
            _localizationStatusText = statusText;
            OnPropertyChanged(nameof(LocalizationStatusText));
        }
    }

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

    public PairStatus VerdictStatus
    {
        get
        {
            if (!_valid)
            {
                return PairStatus.Poor;
            }
            if (_diffRatio <= 1e-12)
            {
                return PairStatus.Mono;
            }
            if (_diffRatio < NearIdenticalDiff || _zeroLag > NearIdenticalCorr)
            {
                return PairStatus.Weak;
            }
            return PairStatus.Good;
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

    /// <summary>Good/weak/poor dot reflecting how reliable the delay estimate is.</summary>
    public PairStatus QualityStatus
    {
        get
        {
            if (!_valid || _levelADb < SignalFloorDb || _levelBDb < SignalFloorDb)
            {
                return PairStatus.Poor;
            }
            if (_coherence >= 0.6) return PairStatus.Good;
            if (_coherence >= 0.3) return PairStatus.Weak;
            return PairStatus.Poor;
        }
    }

    private static double ToDb(double linear)
        => linear <= 1e-7 ? double.NegativeInfinity : 20.0 * Math.Log10(linear);

    private static string FormatDb(double db)
        => double.IsNegativeInfinity(db) ? "  -inf" : $"{db,6:F1}dB";
}
