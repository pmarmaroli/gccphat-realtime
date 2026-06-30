using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using GccPhat.Core;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.Audio;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private AudioDeviceInfo? _selectedDevice;
    private RenderDeviceInfo? _selectedRenderDevice;
    private int? _selectedChannelA;
    private int? _selectedChannelB;
    private int _selectedBufferSize = 4096;
    private int _fmin = 200;
    private int _fmax = 8000;
    private int _updateIntervalMs = 50;
    private bool _yAutoScale = true;
    private double _yMin = -5;
    private double _yMax = 5;
    private bool _xAutoScale = true;
    private double _xWindowSeconds = 20;
    private bool _isRunning;
    private string _statusText = "Select a capture device, then Start. Blow on a mic to identify its channel.";
    private string _detectedChannelText = "Start, then blow on a microphone to identify its channel.";
    private PairViewModel? _selectedPair;
    private string _selectedLayout = "Circular";
    private int _micCount = 6;
    private double _diameterCm = 8;
    private double _spacingCm = 5;
    private bool _hasCenterMic;
    private string _azimuthText = "Azimuth: --";
    private double _levelThresholdDb = -35;
    private double _currentLevelDb = double.NegativeInfinity;

    public MainViewModel()
    {
        Engine = new RealTimeEngine();

        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        AddPairCommand = new RelayCommand(AddPair, CanAddPair);
        RemoveSelectedPairCommand = new RelayCommand(RemoveSelectedPair, () => _selectedPair is not null);
        StartCommand = new RelayCommand(Start, () => !IsRunning && SelectedDevice is not null);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        ListenCommand = new RelayCommand(() => BeamListening = !BeamListening, () => IsRunning);

        ActivePairs.CollectionChanged += OnActivePairsChanged;
        MicPositions.CollectionChanged += OnMicPositionsChanged;

        RefreshDevices();
        RebuildPositions();
        Engine.SetBeamformingMode(ParseBeamformerMode(_selectedBeamformerMode));
        NotifyToolStateChanged();
    }

    public RealTimeEngine Engine { get; }

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();
    public ObservableCollection<RenderDeviceInfo> RenderDevices { get; } = new();
    public ObservableCollection<int> AvailableChannels { get; } = new();
    public ObservableCollection<ChannelMeterViewModel> ChannelMeters { get; } = new();
    public ObservableCollection<PairViewModel> ActivePairs { get; } = new();
    public ObservableCollection<MicGeometryViewModel> MicPositions { get; } = new();
    public int[] BufferSizeOptions { get; } = { 1024, 2048, 4096, 8192, 16384, 32768 };
    public string[] LayoutOptions { get; } = { "Circular", "Linear" };

    public string SelectedLayout
    {
        get => _selectedLayout;
        set { if (SetProperty(ref _selectedLayout, value)) { OnPropertyChanged(nameof(IsCircular)); OnPropertyChanged(nameof(IsLinear)); RebuildPositions(); } }
    }

    public bool IsCircular => _selectedLayout == "Circular";
    public bool IsLinear => _selectedLayout == "Linear";

    public int MicCount
    {
        get => _micCount;
        set { if (SetProperty(ref _micCount, value)) RebuildPositions(); }
    }

    public double DiameterCm
    {
        get => _diameterCm;
        set { if (SetProperty(ref _diameterCm, value)) RebuildPositions(); }
    }

    public double SpacingCm
    {
        get => _spacingCm;
        set { if (SetProperty(ref _spacingCm, value)) RebuildPositions(); }
    }

    public bool HasCenterMic
    {
        get => _hasCenterMic;
        set { if (SetProperty(ref _hasCenterMic, value)) RebuildPositions(); }
    }

    public string AzimuthText
    {
        get => _azimuthText;
        private set => SetProperty(ref _azimuthText, value);
    }

    /// <summary>Gate: the array only appears on the compass when the level is above this (dBFS).</summary>
    public double LevelThresholdDb
    {
        get => _levelThresholdDb;
        set { if (SetProperty(ref _levelThresholdDb, value)) OnPropertyChanged(nameof(IsAboveThreshold)); }
    }

    /// <summary>Loudest channel level (dBFS) of the latest frame; drives the compass gate.</summary>
    public double CurrentLevelDb
    {
        get => _currentLevelDb;
        private set
        {
            if (SetProperty(ref _currentLevelDb, value))
            {
                OnPropertyChanged(nameof(LevelText));
                OnPropertyChanged(nameof(IsAboveThreshold));
            }
        }
    }

    public string SelectedBeamformerMode
    {
        get => _selectedBeamformerMode;
        set
        {
            if (SetProperty(ref _selectedBeamformerMode, value))
            {
                Engine.SetBeamformingMode(ParseBeamformerMode(value));
                OnPropertyChanged(nameof(BeamModeDescriptionText));
                OnPropertyChanged(nameof(BeamModeStatusText));
                OnPropertyChanged(nameof(BeamProcessingText));
            }
        }
    }

    public string BeamModeDescriptionText
        => ParseBeamformerMode(_selectedBeamformerMode) switch
        {
            BeamformerMode.DifferentialAuto =>
                "Differential mode projects the selected microphones onto the steering axis, then picks the highest stable differential order supported by that geometry.",
            _ =>
                "Delay-and-sum aligns the selected microphones toward the look direction, then averages them into a broad, robust listening beam."
        };

    public string BeamModeStatusText
    {
        get
        {
            if (ParseBeamformerMode(_selectedBeamformerMode) != BeamformerMode.DifferentialAuto)
            {
                return "Steering delays are applied to every selected microphone, then the channels are averaged.";
            }

            var selected = MicPositions
                .Where(p => p.Channel is int && p.IncludeInBeam)
                .ToList();
            if (selected.Count < 2)
            {
                return "Auto differential order: need at least 2 assigned microphones in the beam.";
            }

            var x = selected.Select(p => p.X).ToArray();
            var y = selected.Select(p => p.Y).ToArray();
            var weights = new double[selected.Count];
            bool ok = DifferentialBeamformerDesigner.TryBuildWeights(x, y, BeamAzimuthDeg, weights, out DifferentialBeamformerDesign design);
            if (!ok || !design.IsUsable)
            {
                return design.CandidateOrder == 0
                    ? "Auto differential order: 0 — the selected microphones do not span the steering axis at this azimuth."
                    : $"Auto differential order: geometry suggests up to order {design.CandidateOrder}, but that setup is too ill-conditioned at this azimuth.";
            }

            string stability = design.CandidateOrder > design.Order
                ? $"limited to order {design.Order} for stability"
                : $"using order {design.Order}";
            return $"Auto differential order: {stability} ({design.DistinctProjectedPositions} projected positions over {design.ApertureMeters * 100:F1} cm).";
        }
    }

    public string BeamListenButtonText => BeamListening ? "Stop listening" : "Start listening";

    public string BeamListenStateText
        => BeamListening
            ? $"Rendering to: {SelectedRenderDevice?.Name ?? "default output"}"
            : $"Ready to render to: {SelectedRenderDevice?.Name ?? "default output"}";

    public string BeamWorkflowText
        => !HasValidSpatialGeometry
            ? $"Set geometry first: {GeometryIssueText}."
            : !HasValidRenderOutput
                ? "Choose a render speaker/output for the beamformer."
                : IsRunning
                    ? "Step 2: click Start listening to hear the beamformed output."
                    : "Step 1: click START in the main window to begin microphone capture. Then come back here and click Start listening.";

    public string BeamOutputText => SelectedRenderDevice?.Name ?? "Default output";

    public string BeamProcessingText
    {
        get
        {
            int sampleRate = SelectedDevice?.SampleRate ?? 48000;
            double blockMs = SelectedBufferSize * 1000.0 / sampleRate;
            string mode = ParseBeamformerMode(_selectedBeamformerMode) == BeamformerMode.DifferentialAuto
                ? "Mode: auto-order differential."
                : "Mode: delay-and-sum.";
            return $"{mode} Processing block: {SelectedBufferSize} samples (~{blockMs:F1} ms at {sampleRate} Hz), using the Analysis window. Refresh cadence: {UpdateIntervalMs} ms.";
        }
    }

    public string DelayToolRequirementsText => "Needs: at least 1 channel pair, then START.";

    public string DelayToolStatusText
        => ActivePairs.Count < 1
            ? "Missing: add at least 1 channel pair."
            : IsRunning
                ? "Ready now."
                : "Ready after you press START.";

    public string LocalizationToolRequirementsText => "Needs: geometry mapped to unique capture channels, at least 2 channel pairs, then START.";

    public string LocalizationToolStatusText
    {
        get
        {
            if (!HasValidSpatialGeometry)
            {
                return $"Missing: {GeometryIssueText}.";
            }

            if (ActivePairs.Count < 2)
            {
                return ActivePairs.Count == 1
                    ? "Missing: add 1 more channel pair."
                    : "Missing: add at least 2 channel pairs.";
            }

            return IsRunning ? "Ready now." : "Ready after you press START.";
        }
    }

    public string BeamformerToolRequirementsText => "Needs: geometry mapped to unique capture channels, a render speaker/output, START, then Start listening.";

    public string BeamformerToolStatusText
    {
        get
        {
            if (!HasValidSpatialGeometry)
            {
                return $"Missing: {GeometryIssueText}.";
            }

            if (!HasValidRenderOutput)
            {
                return "Missing: choose a render speaker/output.";
            }

            if (!IsRunning)
            {
                return "Ready after you press START.";
            }

            return "Ready now.";
        }
    }

    public bool IsAboveThreshold => _currentLevelDb >= _levelThresholdDb;
    public string LevelText => double.IsNegativeInfinity(_currentLevelDb) ? "Level: -inf dB" : $"Level: {_currentLevelDb,6:F1} dB";

    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand AddPairCommand { get; }
    public RelayCommand RemoveSelectedPairCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ListenCommand { get; }

    public AudioDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                RebuildChannelList();
                OnPropertyChanged(nameof(BeamProcessingText));
                RaiseCommandStates();
            }
        }
    }

    public RenderDeviceInfo? SelectedRenderDevice
    {
        get => _selectedRenderDevice;
        set
        {
            if (SetProperty(ref _selectedRenderDevice, value))
            {
                Engine.SetRenderDevice(_selectedRenderDevice);
                OnPropertyChanged(nameof(BeamOutputText));
                OnPropertyChanged(nameof(BeamListenStateText));
            }
        }
    }

    public int? SelectedChannelA
    {
        get => _selectedChannelA;
        set { if (SetProperty(ref _selectedChannelA, value)) AddPairCommand.RaiseCanExecuteChanged(); }
    }

    public int? SelectedChannelB
    {
        get => _selectedChannelB;
        set { if (SetProperty(ref _selectedChannelB, value)) AddPairCommand.RaiseCanExecuteChanged(); }
    }

    public int SelectedBufferSize
    {
        get => _selectedBufferSize;
        set
        {
            if (SetProperty(ref _selectedBufferSize, value))
            {
                OnPropertyChanged(nameof(BeamProcessingText));
            }
        }
    }

    public int Fmin
    {
        get => _fmin;
        set => SetProperty(ref _fmin, value);
    }

    public int Fmax
    {
        get => _fmax;
        set => SetProperty(ref _fmax, value);
    }

    public int UpdateIntervalMs
    {
        get => _updateIntervalMs;
        set
        {
            if (SetProperty(ref _updateIntervalMs, value))
            {
                OnPropertyChanged(nameof(BeamProcessingText));
            }
        }
    }

    public bool YAutoScale
    {
        get => _yAutoScale;
        set { if (SetProperty(ref _yAutoScale, value)) OnPropertyChanged(nameof(CanEditYRange)); }
    }

    public bool CanEditYRange => !_yAutoScale;

    public double YMin
    {
        get => _yMin;
        set => SetProperty(ref _yMin, value);
    }

    public double YMax
    {
        get => _yMax;
        set => SetProperty(ref _yMax, value);
    }

    public bool XAutoScale
    {
        get => _xAutoScale;
        set { if (SetProperty(ref _xAutoScale, value)) OnPropertyChanged(nameof(CanEditXRange)); }
    }

    public bool CanEditXRange => !_xAutoScale;

    public double XWindowSeconds
    {
        get => _xWindowSeconds;
        set => SetProperty(ref _xWindowSeconds, value);
    }

    public PairViewModel? SelectedPair
    {
        get => _selectedPair;
        set { if (SetProperty(ref _selectedPair, value)) RemoveSelectedPairCommand.RaiseCanExecuteChanged(); }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanEditConfig));
                OnPropertyChanged(nameof(BeamWorkflowText));
                RaiseCommandStates();
            }
        }
    }

    public bool CanEditConfig => !_isRunning;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DetectedChannelText
    {
        get => _detectedChannelText;
        private set => SetProperty(ref _detectedChannelText, value);
    }

    private void RefreshDevices()
    {
        try
        {
            string? previousId = SelectedDevice?.Id;
            string? previousRenderId = SelectedRenderDevice?.Id;
            Devices.Clear();
            foreach (AudioDeviceInfo device in DeviceEnumerator.ListCaptureDevices())
            {
                Devices.Add(device);
            }
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == previousId) ?? Devices.FirstOrDefault();

            string? defaultRenderId = DeviceEnumerator.GetDefaultRenderDeviceId();
            RenderDevices.Clear();
            foreach (RenderDeviceInfo device in DeviceEnumerator.ListRenderDevices())
            {
                RenderDevices.Add(device);
            }
            SelectedRenderDevice =
                RenderDevices.FirstOrDefault(d => d.Id == previousRenderId)
                ?? RenderDevices.FirstOrDefault(d => d.Id == defaultRenderId)
                ?? RenderDevices.FirstOrDefault();

            StatusText = $"Found {Devices.Count} capture device(s) and {RenderDevices.Count} render device(s).";
            NotifyToolStateChanged();
        }
        catch (Exception ex)
        {
            StatusText = $"Device enumeration failed: {ex.Message}";
        }
    }

    private void RebuildChannelList()
    {
        AvailableChannels.Clear();
        ChannelMeters.Clear();
        int count = SelectedDevice?.ChannelCount ?? 0;
        for (int c = 0; c < count; c++)
        {
            AvailableChannels.Add(c);
            ChannelMeters.Add(new ChannelMeterViewModel(c));
        }
        SelectedChannelA = count > 0 ? 0 : null;
        SelectedChannelB = count > 1 ? 1 : (count > 0 ? 0 : null);
        NotifyToolStateChanged();
    }

    private bool CanAddPair()
        => SelectedChannelA is int a
           && SelectedChannelB is int b
           && a != b;

    private void AddPair()
    {
        if (SelectedChannelA is not int a || SelectedChannelB is not int b || a == b)
        {
            return;
        }

        var pair = new ChannelPair(a, b);
        if (ActivePairs.Any(p => p.Pair == pair))
        {
            StatusText = $"Pair {pair} already added.";
            return;
        }

        ActivePairs.Add(new PairViewModel(pair, ActivePairs.Count));
        SyncEnginePairs();
        RaiseCommandStates();
        StatusText = $"Added pair {pair}.";
    }

    private void RemoveSelectedPair()
    {
        if (_selectedPair is null)
        {
            return;
        }
        ActivePairs.Remove(_selectedPair);
        // Reindex palette so colours stay contiguous.
        var snapshot = ActivePairs.ToList();
        ActivePairs.Clear();
        for (int i = 0; i < snapshot.Count; i++)
        {
            ActivePairs.Add(new PairViewModel(snapshot[i].Pair, i));
        }
        SelectedPair = null;
        SyncEnginePairs();
        RaiseCommandStates();
    }

    private void SyncEnginePairs() => Engine.SetPairs(ActivePairs.Select(p => p.Pair));

    // Recomputes mic positions (metres) from the selected array layout and its parameters.
    private void RebuildPositions()
    {
        DetachGeometryPositionHandlers();
        int n = Math.Clamp(_micCount, 2, 64);
        MicPositions.Clear();
        if (IsCircular)
        {
            double r = _diameterCm / 100.0 / 2.0;
            for (int i = 0; i < n; i++)
            {
                double a = 2.0 * Math.PI * i / n;
                MicPositions.Add(new MicGeometryViewModel(i, r * Math.Cos(a), r * Math.Sin(a), AvailableChannels));
            }
            if (_hasCenterMic)
            {
                MicPositions.Add(new MicGeometryViewModel(n, 0.0, 0.0, AvailableChannels, "Center"));
            }
        }
        else
        {
            double d = _spacingCm / 100.0;
            double x0 = -(n - 1) * d / 2.0;
            for (int i = 0; i < n; i++)
            {
                MicPositions.Add(new MicGeometryViewModel(i, x0 + i * d, 0.0, AvailableChannels));
            }
        }
        AttachGeometryPositionHandlers();
        UpdateBeamBand();
        NotifyToolStateChanged();
    }

    // Builds per-channel geometry arrays (metres) from the channel→position assignments.
    private void SyncEngineGeometry()
    {
        int maxChannel = MicPositions.Where(p => p.Channel is int).Select(p => p.Channel!.Value).DefaultIfEmpty(-1).Max();
        if (maxChannel < 0)
        {
            return;
        }
        var x = new double[maxChannel + 1];
        var y = new double[maxChannel + 1];
        foreach (MicGeometryViewModel pos in MicPositions)
        {
            if (pos.Channel is int ch)
            {
                x[ch] = pos.X;
                y[ch] = pos.Y;
            }
        }
        Engine.SetGeometry(x, y);
    }

    /// <summary>Tells the engine which capture channels feed the beamformer (assigned + included).</summary>
    private void SyncBeamChannels()
    {
        int maxChannel = MicPositions.Where(p => p.Channel is int).Select(p => p.Channel!.Value).DefaultIfEmpty(-1).Max();
        if (maxChannel < 0)
        {
            Engine.SetBeamChannels(System.Array.Empty<bool>());
            return;
        }
        var include = new bool[maxChannel + 1];
        foreach (MicGeometryViewModel pos in MicPositions)
        {
            if (pos.Channel is int ch && pos.IncludeInBeam)
            {
                include[ch] = true;
            }
        }
        Engine.SetBeamChannels(include);
        UpdateBeamBand();
    }

    /// <summary>
    /// Derives the beamformer's useful spatial passband from the array geometry and pushes it to the
    /// engine: f_low = c/(2·aperture) (below this there's no directivity), f_high = c/(2·min-spacing)
    /// (above this spatial aliasing). Uses only mics that are both assigned and included in the beam.
    /// </summary>
    private void UpdateBeamBand()
    {
        const double speed = 343.0; // m/s
        var pts = MicPositions
            .Where(p => p.Channel is int && p.IncludeInBeam)
            .Select(p => (p.X, p.Y))
            .ToList();

        if (!_beamBandLimit || pts.Count < 2)
        {
            Engine.SetBeamBand(0.0, double.PositiveInfinity);
            BeamBandText = pts.Count < 2
                ? "Full band — assign ≥ 2 mics to estimate the useful band."
                : "Full band (no spatial filter).";
            return;
        }

        double dMax = 0.0, dMin = double.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            for (int j = i + 1; j < pts.Count; j++)
            {
                double dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > dMax) dMax = dist;
                if (dist > 0 && dist < dMin) dMin = dist;
            }
        }

        if (dMax <= 0 || dMin == double.MaxValue)
        {
            Engine.SetBeamBand(0.0, double.PositiveInfinity);
            BeamBandText = "Full band (coincident mics).";
            return;
        }

        double fLow = speed / (2.0 * dMax);
        double fHigh = speed / (2.0 * dMin);
        Engine.SetBeamBand(fLow, fHigh);
        BeamBandText = $"Useful band: {fLow:F0}–{fHigh:F0} Hz  (aperture {dMax * 100:F1} cm, min spacing {dMin * 100:F1} cm)";
    }

    private double _azimuthDeg;
    private bool _beamListening;
    private double _beamAzimuthDeg;
    private double _beamGainDb;
    private bool _beamBandLimit = true;
    private string _beamBandText = "Full band.";
    private string _selectedBeamformerMode = BeamformerModeDelayAndSum;

    private const string BeamformerModeDelayAndSum = "Delay-and-sum";
    private const string BeamformerModeDifferentialAuto = "Differential (auto order)";

    public double AzimuthDeg
    {
        get => _azimuthDeg;
        private set => SetProperty(ref _azimuthDeg, value);
    }

    public string[] BeamformerModeOptions { get; } =
    {
        BeamformerModeDelayAndSum,
        BeamformerModeDifferentialAuto
    };

    public bool BeamListening
    {
        get => _beamListening;
        set
        {
            if (value && !IsRunning)
            {
                StatusText = "Start capture first, then start beam listening.";
                return;
            }

            if (SetProperty(ref _beamListening, value))
            {
                if (_beamListening)
                {
                    Engine.StartListening();
                }
                else
                {
                    Engine.StopListening();
                }

                OnPropertyChanged(nameof(BeamListenButtonText));
                OnPropertyChanged(nameof(BeamListenStateText));
            }
        }
    }

    public double BeamAzimuthDeg
    {
        get => _beamAzimuthDeg;
        set
        {
            if (SetProperty(ref _beamAzimuthDeg, value))
            {
                Engine.SetBeamformingAzimuth(_beamAzimuthDeg);
                OnPropertyChanged(nameof(BeamModeStatusText));
            }
        }
    }

    /// <summary>Output gain boost (dB) applied to the beamformed stream.</summary>
    public double BeamGainDb
    {
        get => _beamGainDb;
        set
        {
            if (SetProperty(ref _beamGainDb, value))
            {
                Engine.SetBeamGain(Math.Pow(10.0, _beamGainDb / 20.0));
                OnPropertyChanged(nameof(BeamGainText));
            }
        }
    }

    public string BeamGainText => $"+{_beamGainDb:F0} dB";

    /// <summary>When on, the beamformer output is limited to the array's useful spatial passband.</summary>
    public bool BeamBandLimit
    {
        get => _beamBandLimit;
        set
        {
            if (SetProperty(ref _beamBandLimit, value))
            {
                UpdateBeamBand();
            }
        }
    }

    public string BeamBandText
    {
        get => _beamBandText;
        private set => SetProperty(ref _beamBandText, value);
    }

    /// <summary>Called on the UI thread with the latest SRP-PHAT azimuth over the active pairs.</summary>
    public void UpdateAzimuth(SrpEstimate estimate)
    {
        AzimuthDeg = estimate.AzimuthDeg;
        AzimuthText = $"Source azimuth: {estimate.AzimuthDeg,6:F1}\u00b0";
    }

    private void Start()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            var capture = new MultichannelCapture(SelectedDevice);

            int nyquist = capture.SampleRate / 2;
            int fmax = Math.Min(Fmax, nyquist);
            if (fmax != Fmax)
            {
                Fmax = fmax;
            }

            Engine.Configure(SelectedBufferSize, Fmin, fmax, UpdateIntervalMs);
            SyncEnginePairs();
            SyncEngineGeometry();
            SyncBeamChannels();
            Engine.Start(capture);

            IsRunning = true;
            StatusText = $"Running on \"{SelectedDevice.Name}\" — {capture.ChannelCount} ch @ {capture.SampleRate} Hz, "
                       + $"window {SelectedBufferSize}, band {Fmin}\u2013{fmax} Hz.";
        }
        catch (Exception ex)
        {
            IsRunning = false;
            string hint = SelectedDevice.UseExclusive
                ? " This device needs exclusive mode for all channels; close any app using it and retry."
                : string.Empty;
            StatusText = $"Failed to start capture: {ex.Message}.{hint}";
        }
    }

    private void Stop()
    {
        Engine.Stop();
        BeamListening = false;
        IsRunning = false;
        foreach (PairViewModel pair in ActivePairs)
        {
            pair.ClearLive();
        }
        foreach (ChannelMeterViewModel meter in ChannelMeters)
        {
            meter.SetLevel(0.0);
            meter.IsActive = false;
        }
        DetectedChannelText = "Start, then blow on a microphone to identify its channel.";
        AzimuthText = "Azimuth: --";
        CurrentLevelDb = double.NegativeInfinity;
        StatusText = "Stopped.";
    }

    /// <summary>Called on the UI thread to refresh per-pair readouts from the latest results.</summary>
    public void UpdateReadouts(IReadOnlyList<PairResult> results)
    {
        foreach (PairResult result in results)
        {
            PairViewModel? vm = ActivePairs.FirstOrDefault(p => p.Pair == result.Pair);
            vm?.SetLive(result);
        }
    }

    /// <summary>Called on the UI thread with per-channel levels to drive the identify meters.</summary>
    public void UpdateChannelLevels(double[] levels)
    {
        const double ActivationDb = -35.0;
        const double MarginDb = 6.0;

        int n = Math.Min(levels.Length, ChannelMeters.Count);
        int bestIndex = -1;
        double bestDb = double.NegativeInfinity;
        double secondDb = double.NegativeInfinity;

        for (int c = 0; c < n; c++)
        {
            ChannelMeters[c].SetLevel(levels[c]);
            double db = levels[c] <= 1e-7 ? double.NegativeInfinity : 20.0 * Math.Log10(levels[c]);
            if (db > bestDb)
            {
                secondDb = bestDb;
                bestDb = db;
                bestIndex = c;
            }
            else if (db > secondDb)
            {
                secondDb = db;
            }
        }

        bool detected = bestIndex >= 0
                        && bestDb > ActivationDb
                        && (double.IsNegativeInfinity(secondDb) || bestDb - secondDb > MarginDb);

        CurrentLevelDb = bestDb;

        for (int c = 0; c < ChannelMeters.Count; c++)
        {
            ChannelMeters[c].IsActive = detected && c == bestIndex;
        }

        DetectedChannelText = detected
            ? $"\u27a1 Channel {bestIndex} is loudest \u2014 that microphone is channel {bestIndex}."
            : "Blow on a microphone to identify its channel\u2026";
    }

    private void RaiseCommandStates()
    {
        AddPairCommand.RaiseCanExecuteChanged();
        RemoveSelectedPairCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ListenCommand.RaiseCanExecuteChanged();
    }

    private bool HasValidRenderOutput => SelectedRenderDevice is not null;

    private bool HasValidSpatialGeometry
        => MicPositions.Count >= 2
           && MicPositions.All(p => p.Channel is int ch && AvailableChannels.Contains(ch))
           && MicPositions
               .Where(p => p.Channel is int)
               .Select(p => p.Channel!.Value)
               .Distinct()
               .Count() == MicPositions.Count;

    private string GeometryIssueText
    {
        get
        {
            if (MicPositions.Count < 2)
            {
                return "set at least 2 microphone positions";
            }

            if (MicPositions.Any(p => p.Channel is not int))
            {
                return "assign every geometry position to a capture channel";
            }

            if (MicPositions.Any(p => p.Channel is int ch && !AvailableChannels.Contains(ch)))
            {
                return "fix channel assignments so they match the selected capture device";
            }

            if (MicPositions
                .Where(p => p.Channel is int)
                .Select(p => p.Channel!.Value)
                .GroupBy(ch => ch)
                .Any(g => g.Count() > 1))
            {
                return "use a unique capture channel for each geometry position";
            }

            return "geometry is incomplete";
        }
    }

    private void OnActivePairsChanged(object? sender, NotifyCollectionChangedEventArgs e) => NotifyToolStateChanged();

    private void OnMicPositionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        AttachGeometryPositionHandlers();
        NotifyToolStateChanged();
        OnPropertyChanged(nameof(BeamModeStatusText));
    }

    private void OnMicPositionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MicGeometryViewModel.Channel))
        {
            SyncBeamChannels();
            NotifyToolStateChanged();
            OnPropertyChanged(nameof(BeamModeStatusText));
        }
        else if (e.PropertyName == nameof(MicGeometryViewModel.IncludeInBeam))
        {
            SyncBeamChannels();
            OnPropertyChanged(nameof(BeamModeStatusText));
        }
    }

    private void AttachGeometryPositionHandlers()
    {
        foreach (MicGeometryViewModel pos in MicPositions)
        {
            pos.PropertyChanged -= OnMicPositionPropertyChanged;
            pos.PropertyChanged += OnMicPositionPropertyChanged;
        }
    }

    private void DetachGeometryPositionHandlers()
    {
        foreach (MicGeometryViewModel pos in MicPositions)
        {
            pos.PropertyChanged -= OnMicPositionPropertyChanged;
        }
    }

    private void NotifyToolStateChanged()
    {
        OnPropertyChanged(nameof(DelayToolStatusText));
        OnPropertyChanged(nameof(LocalizationToolStatusText));
        OnPropertyChanged(nameof(BeamformerToolStatusText));
        OnPropertyChanged(nameof(BeamWorkflowText));
        OnPropertyChanged(nameof(BeamModeStatusText));
    }

    private static BeamformerMode ParseBeamformerMode(string mode)
        => mode == BeamformerModeDifferentialAuto
            ? BeamformerMode.DifferentialAuto
            : BeamformerMode.DelayAndSum;
}
