using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

    private MainViewModel? _sessionA;
    private MainViewModel? _sessionB;
    private double _offsetXCm;
    private double _offsetYCm;
    private double _offsetZCm;
    private string _fixStatusText = "Pick two analysis windows to combine.";
    private double? _sourceXCm;
    private double? _sourceYCm;

    public CombinedLocalizationViewModel()
    {
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
            Unhook(_sessionA);
            _sessionA = value;
            Hook(_sessionA);
            OnPropertyChanged();
            Recompute();
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
            Unhook(_sessionB);
            _sessionB = value;
            Hook(_sessionB);
            OnPropertyChanged();
            Recompute();
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

    public void Dispose()
    {
        MainViewModel.OpenSessionsChanged -= OnOpenSessionsChanged;
        Unhook(_sessionA);
        Unhook(_sessionB);
    }
}
