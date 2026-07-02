using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using GccPhat.Core;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime.ViewModels;

/// <summary>
/// Combines the live SRP-PHAT azimuth from two open analysis windows (sessions) into a single
/// 2D (x, y) source fix, given the physical offset between the two arrays. Assumes both arrays
/// share the same orientation (no relative rotation) — only a position offset is modeled. The
/// z offset is captured for record-keeping only: the underlying SRP-PHAT pipeline has no genuine
/// elevation bearing to combine it with, so it does not affect the fix.
/// </summary>
public sealed class CombinedLocalizationViewModel : ObservableObject, IDisposable
{
    // |sin(azA - azB)| below this is treated as parallel bearings (no usable fix).
    private const double ParallelEpsilon = 0.001;

    // Sync calibration: capture windows, broadband band, and quality gates. Calibration uses a
    // larger backward-looking window than the live cross-check because it's a "clap, then click"
    // gesture — the transient needs to still be inside the "latest N samples" grab by the time the
    // user reacts and clicks the button.
    private const int CalibrationWindowSamples = 32768; // ~683 ms @ 48 kHz
    private const int SyncWindowSamples = 8192;          // ~171 ms @ 48 kHz
    private const int SyncFmin = 200;
    private const int SyncFmaxCap = 8000;
    private const double MinCalibrationCoherence = 0.3;
    private const double CrossCheckGoodToleranceSamples = 2.0;
    private const string SyncIdleText = "Bring the two arrays' closest mics together, position your mouse over Measure sync, clap once near the touching mics, then click immediately.";

    private static readonly Brush GoodBrush = Freeze(Color.FromRgb(44, 160, 44));
    private static readonly Brush WarnBrush = Freeze(Color.FromRgb(214, 154, 39));

    private MainViewModel? _sessionA;
    private MainViewModel? _sessionB;
    private double _offsetXCm;
    private double _offsetYCm;
    private double _offsetZCm;
    private string _fixStatusText = "Pick two analysis windows to combine.";
    private double? _sourceXCm;
    private double? _sourceYCm;

    private int? _syncChannelA;
    private int? _syncChannelB;
    private SyncCalibration? _calibration;
    private string _syncStatusText = SyncIdleText;
    private string _crossCheckText = "Cross-check: calibrate sync first.";
    private Brush _crossCheckBrush = WarnBrush;
    private readonly DispatcherTimer _crossCheckTimer;

    public CombinedLocalizationViewModel()
    {
        _crossCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _crossCheckTimer.Tick += (_, _) => UpdateCrossCheck();
        MeasureSyncCommand = new RelayCommand(MeasureSync, CanMeasureSync);

        RefreshSessions();
        MainViewModel.OpenSessionsChanged += OnOpenSessionsChanged;
    }

    public ObservableCollection<MainViewModel> AvailableSessions { get; } = new();

    public MainViewModel? SessionA
    {
        get => _sessionA;
        set
        {
            if (ReferenceEquals(_sessionA, value))
            {
                return;
            }
            ClearCalibration();
            Unhook(_sessionA);
            _sessionA = value;
            Hook(_sessionA);
            OnPropertyChanged();
            Recompute();
            MeasureSyncCommand.RaiseCanExecuteChanged();
        }
    }

    public MainViewModel? SessionB
    {
        get => _sessionB;
        set
        {
            if (ReferenceEquals(_sessionB, value))
            {
                return;
            }
            ClearCalibration();
            Unhook(_sessionB);
            _sessionB = value;
            Hook(_sessionB);
            OnPropertyChanged();
            Recompute();
            MeasureSyncCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Position of array B relative to array A, along A's X axis (cm).</summary>
    public double OffsetXCm
    {
        get => _offsetXCm;
        set { if (SetProperty(ref _offsetXCm, value)) Recompute(); }
    }

    /// <summary>Position of array B relative to array A, along A's Y axis (cm).</summary>
    public double OffsetYCm
    {
        get => _offsetYCm;
        set { if (SetProperty(ref _offsetYCm, value)) Recompute(); }
    }

    /// <summary>
    /// Vertical offset of array B relative to array A (cm) — informational only. The SRP-PHAT
    /// pipeline reports a single horizontal bearing per array with no elevation angle, so there is
    /// nothing to combine this with; it does not feed into <see cref="SourceXCm"/>/<see cref="SourceYCm"/>.
    /// </summary>
    public double OffsetZCm
    {
        get => _offsetZCm;
        set => SetProperty(ref _offsetZCm, value);
    }

    public string FixStatusText
    {
        get => _fixStatusText;
        private set => SetProperty(ref _fixStatusText, value);
    }

    public double? SourceXCm
    {
        get => _sourceXCm;
        private set => SetProperty(ref _sourceXCm, value);
    }

    public double? SourceYCm
    {
        get => _sourceYCm;
        private set => SetProperty(ref _sourceYCm, value);
    }

    /// <summary>True while fewer than two analysis windows are open — drives the "open another window" hint.</summary>
    public bool NeedsMoreSessions => AvailableSessions.Count < 2;

    /// <summary>Sync calibration mic on Session A — pick the mic physically closest to Session B when calibrating.</summary>
    public int? SyncChannelA
    {
        get => _syncChannelA;
        set { if (SetProperty(ref _syncChannelA, value)) MeasureSyncCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>Sync calibration mic on Session B — pick the mic physically closest to Session A when calibrating.</summary>
    public int? SyncChannelB
    {
        get => _syncChannelB;
        set { if (SetProperty(ref _syncChannelB, value)) MeasureSyncCommand.RaiseCanExecuteChanged(); }
    }

    public string SyncStatusText
    {
        get => _syncStatusText;
        private set => SetProperty(ref _syncStatusText, value);
    }

    public string CrossCheckText
    {
        get => _crossCheckText;
        private set => SetProperty(ref _crossCheckText, value);
    }

    public Brush CrossCheckBrush
    {
        get => _crossCheckBrush;
        private set => SetProperty(ref _crossCheckBrush, value);
    }

    public bool HasCalibration => _calibration is not null;

    public string CalibrationSummaryText => _calibration is SyncCalibration c
        ? $"Synced Ch{c.ChannelA}↔Ch{c.ChannelB}: offset {c.OffsetSamples:F0} samples (coherence {c.Coherence:F2}), {FormatElapsed(DateTime.UtcNow - c.MeasuredAtUtc)} ago."
        : "Not calibrated yet.";

    public string MeasureSyncButtonText => HasCalibration ? "Recalibrate" : "Measure sync";

    public RelayCommand MeasureSyncCommand { get; }

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalSeconds < 60 ? $"{elapsed.TotalSeconds:F0}s" : $"{elapsed.TotalMinutes:F0}m";

    private void OnOpenSessionsChanged() => RefreshSessions();

    private void RefreshSessions()
    {
        AvailableSessions.Clear();
        foreach (MainViewModel session in MainViewModel.OpenSessions)
        {
            AvailableSessions.Add(session);
        }
        OnPropertyChanged(nameof(NeedsMoreSessions));

        if (SessionA is not null && !AvailableSessions.Contains(SessionA))
        {
            SessionA = null;
        }
        if (SessionB is not null && !AvailableSessions.Contains(SessionB))
        {
            SessionB = null;
        }
        Recompute();
    }

    private void Hook(MainViewModel? session)
    {
        if (session is not null)
        {
            session.PropertyChanged += OnSessionPropertyChanged;
        }
    }

    private void Unhook(MainViewModel? session)
    {
        if (session is not null)
        {
            session.PropertyChanged -= OnSessionPropertyChanged;
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.AzimuthDeg) or nameof(MainViewModel.HasVisibleLocalizationAzimuth))
        {
            Recompute();
        }
        if (e.PropertyName == nameof(MainViewModel.IsRunning))
        {
            // Stopping either session recreates its capture stream on the next Start, which
            // invalidates any previously measured clock offset — not just a session swap does.
            if (sender is MainViewModel session && !session.IsRunning)
            {
                ClearCalibration();
            }
            MeasureSyncCommand.RaiseCanExecuteChanged();
        }
    }

    // Solves the two-ray intersection: ray A from (0,0) at SessionA's azimuth, ray B from the
    // user-entered offset at SessionB's azimuth. Standard 2-line linear solve; det = sin(azA - azB).
    private void Recompute()
    {
        if (_sessionA is null || _sessionB is null)
        {
            SetNoFix("Pick two analysis windows to combine.");
            return;
        }

        if (ReferenceEquals(_sessionA, _sessionB))
        {
            SetNoFix("Pick two different analysis windows.");
            return;
        }

        if (!_sessionA.HasVisibleLocalizationAzimuth || !_sessionB.HasVisibleLocalizationAzimuth)
        {
            SetNoFix("Both arrays need a live azimuth (running, above threshold, localization ready).");
            return;
        }

        double azA = _sessionA.AzimuthDeg * Math.PI / 180.0;
        double azB = _sessionB.AzimuthDeg * Math.PI / 180.0;
        double dAx = Math.Cos(azA), dAy = Math.Sin(azA);
        double dBx = Math.Cos(azB), dBy = Math.Sin(azB);
        double bx = _offsetXCm / 100.0;
        double by = _offsetYCm / 100.0;

        double det = dBx * dAy - dAx * dBy; // = sin(azA - azB)
        if (Math.Abs(det) < ParallelEpsilon)
        {
            SetNoFix("Bearings are parallel — no fix.");
            return;
        }

        double t = (dBx * by - dBy * bx) / det;
        double s = (dAx * by - dAy * bx) / det;
        double sourceXM = t * dAx;
        double sourceYM = t * dAy;

        SourceXCm = sourceXM * 100.0;
        SourceYCm = sourceYM * 100.0;
        FixStatusText = t >= 0 && s >= 0
            ? $"Fix: ({SourceXCm:F1}, {SourceYCm:F1}) cm from array A."
            : $"Fix: ({SourceXCm:F1}, {SourceYCm:F1}) cm from array A — behind one array, check azimuths/offset.";
    }

    private void SetNoFix(string message)
    {
        SourceXCm = null;
        SourceYCm = null;
        FixStatusText = message;
    }

    private bool CanMeasureSync()
        => _sessionA is not null && _sessionB is not null && !ReferenceEquals(_sessionA, _sessionB)
           && _sessionA.IsRunning && _sessionB.IsRunning
           && _syncChannelA is int && _syncChannelB is int;

    // One-shot clock-offset measurement: captures a broadband window from each session's sync
    // channel and cross-correlates it with GCC-PHAT. Intended to be run while the two arrays'
    // sync mics are physically touching, so the measured delay is ~ pure clock/stream offset
    // rather than a real acoustic delay.
    private void MeasureSync()
    {
        if (!CanMeasureSync())
        {
            SyncStatusText = "Pick two distinct, running sessions and a sync channel on each first.";
            return;
        }

        MainViewModel sessionA = _sessionA!;
        MainViewModel sessionB = _sessionB!;
        int channelA = _syncChannelA!.Value;
        int channelB = _syncChannelB!.Value;

        int fsA = sessionA.Engine.SampleRate;
        int fsB = sessionB.Engine.SampleRate;
        if (fsA <= 0 || fsB <= 0)
        {
            SyncStatusText = "One of the sessions isn't capturing yet.";
            return;
        }
        if (fsA != fsB)
        {
            SyncStatusText = $"Sessions have different sample rates ({fsA} vs {fsB} Hz) — sync calibration needs matching rates.";
            return;
        }

        var bufA = new double[CalibrationWindowSamples];
        var bufB = new double[CalibrationWindowSamples];
        if (!sessionA.Engine.TryCopyLatestChannel(channelA, bufA) || !sessionB.Engine.TryCopyLatestChannel(channelB, bufB))
        {
            SyncStatusText = "Not enough audio yet on one of the sync channels.";
            return;
        }

        int fmax = Math.Min(SyncFmaxCap, fsA / 2);
        // (channel1 = A, channel2 = B) — the live cross-check must reuse this exact argument order,
        // or OffsetSamples' sign convention silently inverts.
        DelayEstimate est = GccPhatAnalyzer.Estimate(bufA, bufB, fsA, SyncFmin, fmax);
        double offsetSamples = Math.Round(est.DelayMs / 1000.0 * fsA);

        _calibration = new SyncCalibration(channelA, channelB, offsetSamples, est.Coherence, DateTime.UtcNow);
        SyncStatusText = est.Coherence < MinCalibrationCoherence
            ? $"Low coherence ({est.Coherence:F2}) — clap louder/closer to the touching mics and Recalibrate."
            : $"Synced: offset {offsetSamples:F0} samples (coherence {est.Coherence:F2}).";

        OnPropertyChanged(nameof(HasCalibration));
        OnPropertyChanged(nameof(CalibrationSummaryText));
        OnPropertyChanged(nameof(MeasureSyncButtonText));
        MeasureSyncCommand.RaiseCanExecuteChanged();

        _crossCheckTimer.Start();
        UpdateCrossCheck();
    }

    // Throttled (timer-driven, not per-tick) so it doesn't reintroduce the kind of CPU cost a
    // 20-40 Hz live GCC-PHAT call would add on top of the cheap trig in Recompute().
    private void UpdateCrossCheck()
    {
        if (_calibration is not SyncCalibration calibration)
        {
            CrossCheckText = "Cross-check: calibrate sync first.";
            CrossCheckBrush = WarnBrush;
            return;
        }
        if (_sessionA is null || _sessionB is null)
        {
            CrossCheckText = "Cross-check: needs both sessions.";
            CrossCheckBrush = WarnBrush;
            return;
        }
        if (SourceXCm is not double sourceXCm || SourceYCm is not double sourceYCm)
        {
            CrossCheckText = "Cross-check: needs a live triangulated fix.";
            CrossCheckBrush = WarnBrush;
            OnPropertyChanged(nameof(CalibrationSummaryText));
            return;
        }

        int fs = _sessionA.Engine.SampleRate;
        if (fs <= 0)
        {
            CrossCheckText = "Cross-check: session A isn't capturing.";
            CrossCheckBrush = WarnBrush;
            return;
        }

        if (!TryGetMappedMicPositionMeters(_sessionA, calibration.ChannelA, out double micAx, out double micAy))
        {
            CrossCheckText = "Cross-check: sync channel A isn't a mapped array mic — pick one from Geometry.";
            CrossCheckBrush = WarnBrush;
            return;
        }
        if (!TryGetMappedMicPositionMeters(_sessionB, calibration.ChannelB, out double micBxLocal, out double micByLocal))
        {
            CrossCheckText = "Cross-check: sync channel B isn't a mapped array mic — pick one from Geometry.";
            CrossCheckBrush = WarnBrush;
            return;
        }

        var bufA = new double[SyncWindowSamples];
        var bufB = new double[SyncWindowSamples];
        if (!_sessionA.Engine.TryCopyLatestChannel(calibration.ChannelA, bufA)
            || !_sessionB.Engine.TryCopyLatestChannel(calibration.ChannelB, bufB))
        {
            CrossCheckText = "Cross-check: not enough audio on the sync channels.";
            CrossCheckBrush = WarnBrush;
            return;
        }

        int fmax = Math.Min(SyncFmaxCap, fs / 2);
        // Same (A, B) argument order as MeasureSync() — required for the offset subtraction below
        // to have consistent sign.
        DelayEstimate live = GccPhatAnalyzer.Estimate(bufA, bufB, fs, SyncFmin, fmax);
        double measuredSamples = live.DelayMs / 1000.0 * fs;
        double correctedSamples = measuredSamples - calibration.OffsetSamples;

        double micBxGlobal = micBxLocal + _offsetXCm / 100.0;
        double micByGlobal = micByLocal + _offsetYCm / 100.0;
        double predictedSeconds = NearFieldTdoa.PredictedDelaySeconds(
            micAx, micAy, micBxGlobal, micByGlobal, sourceXCm / 100.0, sourceYCm / 100.0);
        double predictedSamples = predictedSeconds * fs;

        double diffSamples = correctedSamples - predictedSamples;
        CrossCheckBrush = Math.Abs(diffSamples) <= CrossCheckGoodToleranceSamples ? GoodBrush : WarnBrush;
        CrossCheckText =
            $"Cross-check: predicted {predictedSamples:F1} samples, measured {correctedSamples:F1} samples, " +
            $"diff {diffSamples:F1} samples (coherence {live.Coherence:F2}).";

        OnPropertyChanged(nameof(CalibrationSummaryText));
    }

    private static bool TryGetMappedMicPositionMeters(MainViewModel session, int channel, out double x, out double y)
    {
        foreach (MicGeometryViewModel pos in session.MicPositions)
        {
            if (pos.Channel == channel)
            {
                x = pos.X;
                y = pos.Y;
                return true;
            }
        }
        x = 0;
        y = 0;
        return false;
    }

    private void ClearCalibration()
    {
        if (_calibration is null)
        {
            return;
        }
        _calibration = null;
        _crossCheckTimer.Stop();
        SyncStatusText = SyncIdleText;
        CrossCheckText = "Cross-check: calibrate sync first.";
        CrossCheckBrush = WarnBrush;
        OnPropertyChanged(nameof(HasCalibration));
        OnPropertyChanged(nameof(CalibrationSummaryText));
        OnPropertyChanged(nameof(MeasureSyncButtonText));
        MeasureSyncCommand.RaiseCanExecuteChanged();
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        _crossCheckTimer.Stop();
        MainViewModel.OpenSessionsChanged -= OnOpenSessionsChanged;
        Unhook(_sessionA);
        Unhook(_sessionB);
    }
}
