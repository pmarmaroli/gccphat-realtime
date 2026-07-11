using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using GccPhat.Core;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.Audio;
using GccPhat.RealTime.Mvvm;
using GccPhat.RealTime.Presets;
using NAudio.Wave;

namespace GccPhat.RealTime.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    // Cross-window bookkeeping: which capture device (by Id) each open analysis window has
    // claimed, so a newly opened window's device list excludes devices already in use elsewhere.
    private static readonly Dictionary<string, MainViewModel> s_claimedDevices = new();

    // Registry of all currently open analysis windows, so a combined-localization window can
    // discover and pick from them.
    private static readonly List<MainViewModel> s_openSessions = new();
    public static IReadOnlyList<MainViewModel> OpenSessions => s_openSessions;
    public static event Action? OpenSessionsChanged;

    private string? _claimedDeviceId;
    private AudioDeviceInfo? _selectedDevice;
    private RenderDeviceInfo? _selectedRenderDevice;
    private int? _selectedChannelA;
    private int? _selectedChannelB;
    private int _selectedBufferSize = 8192;
    private int _fmin = 200;
    private int _fmax = 8000;
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
    private bool _hasCenterMic = true;
    private bool _hasFrontBackAmbiguity;
    private string _azimuthText = "Azimuth: --";
    private double _levelThresholdDb = -70;
    private bool _localizationPairsAutoApplied;
    private double _currentLevelDb = double.NegativeInfinity;
    private bool _hasLiveAzimuth;
    private string _customInputMode = "Cartesian";
    private bool _micCountLocked;
    private string _captureMode = "Live";
    private string? _replayFilePath;
    private string _replayChannelsDetectedText = "Channels detected: —";
    private int _replaySampleRate;
    private WavFileReplayCapture? _activeReplay;
    private bool _replayEndedNaturally;
    private string? _selectedPresetName;
    private string _newPresetName = "";

    private readonly PresetStore _presetStore = new();

    public MainViewModel()
    {
        Engine = new RealTimeEngine();

        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        AddPairCommand = new RelayCommand(AddPair, CanAddPair);
        RemoveSelectedPairCommand = new RelayCommand(RemoveSelectedPair, () => _selectedPair is not null);
        AddOppositePairsCommand = new RelayCommand(AddOppositePairs);
        AddConsecutivePairsCommand = new RelayCommand(AddConsecutivePairs);
        AddAllPairsCommand = new RelayCommand(AddAllPairs);
        StartCommand = new RelayCommand(() => Start(), () => !IsRunning && (IsLiveMode ? SelectedDevice is not null : ReplayFilePath is not null));
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        ListenCommand = new RelayCommand(() => BeamListening = !BeamListening, () => IsRunning);
        LoadPresetCommand = new RelayCommand(LoadPreset, () => SelectedPresetName is not null);
        SavePresetCommand = new RelayCommand(SavePreset, () => !string.IsNullOrWhiteSpace(NewPresetName) || SelectedPresetName is not null);
        SaveAsPresetCommand = new RelayCommand(SaveAsPreset, () => !string.IsNullOrWhiteSpace(NewPresetName));
        DeletePresetCommand = new RelayCommand(DeletePreset, () => SelectedPresetName is not null);

        ActivePairs.CollectionChanged += OnActivePairsChanged;
        MicPositions.CollectionChanged += OnMicPositionsChanged;

        RefreshDevices();
        RebuildPositions();
        RefreshPresetNames();
        Engine.SetBeamformingMode(ParseBeamformerMode(_selectedBeamformerMode));
        Engine.SetLevelThresholdDb(_levelThresholdDb);
        NotifyToolStateChanged();

        // Loads the YAMNet ONNX model off the UI thread: model loading can take several seconds, and
        // since every open analysis window shares one WPF UI thread, loading it synchronously here
        // would freeze ALL open windows for that duration every time a new one is opened.
        ClassificationVm.StatusText = "Loading YAMNet…";
        Dispatcher uiDispatcher = Dispatcher.CurrentDispatcher;
        Task.Run(() =>
        {
            Classifier.Load();
            uiDispatcher.Invoke(() =>
            {
                ClassificationVm.StatusText = Classifier.StatusText;
                if (Classifier.IsAvailable)
                {
                    Engine.SetClassifier(Classifier, _classificationChannel);
                }
            });
        });

        s_openSessions.Add(this);
        OpenSessionsChanged?.Invoke();
    }

    public RealTimeEngine Engine { get; }

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();
    public ObservableCollection<RenderDeviceInfo> RenderDevices { get; } = new();
    public ObservableCollection<int> AvailableChannels { get; } = new();
    public ObservableCollection<ChannelMeterViewModel> ChannelMeters { get; } = new();
    public ObservableCollection<PairViewModel> ActivePairs { get; } = new();
    public ObservableCollection<MicGeometryViewModel> MicPositions { get; } = new();
    public int[] BufferSizeOptions { get; } = { 1024, 2048, 4096, 8192, 16384, 32768 };
    public string[] LayoutOptions { get; } = { "Circular", "Linear", "Custom" };
    public ObservableCollection<string> PresetNames { get; } = new();

    public string[] QuickPresetOptions { get; } =
    {
        "Quick preset…",
        "4-mic linear, 4 cm (broadside)",
        "6-mic circular, 8 cm + center"
    };

    /// <summary>Applies the selected quick preset, then resets the picker back to the placeholder
    /// (this is a one-shot action menu, not a persistent selection).</summary>
    public string SelectedQuickPreset
    {
        get => QuickPresetOptions[0];
        set
        {
            switch (value)
            {
                case "4-mic linear, 4 cm (broadside)":
                    ApplyLinearBroadsidePreset();
                    break;
                case "6-mic circular, 8 cm + center":
                    ApplyCircular6CenterPreset();
                    break;
            }
            OnPropertyChanged(nameof(SelectedQuickPreset));
        }
    }

    public string SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (SetProperty(ref _selectedLayout, value))
            {
                OnPropertyChanged(nameof(IsCircular));
                OnPropertyChanged(nameof(IsLinear));
                OnPropertyChanged(nameof(IsCustom));
                OnPropertyChanged(nameof(IsNotCustom));
                RebuildPositions();
            }
        }
    }

    public bool IsCircular => _selectedLayout == "Circular";
    public bool IsLinear => _selectedLayout == "Linear";
    public bool IsCustom => _selectedLayout == "Custom";
    public bool IsNotCustom => !IsCustom;

    /// <summary>Array-wide coordinate entry mode for the Custom layout ("Cartesian" or "Spherical").
    /// Switching mode re-derives each position's other representation so no data is lost.</summary>
    public string CustomInputMode
    {
        get => _customInputMode;
        set
        {
            if (SetProperty(ref _customInputMode, value))
            {
                foreach (MicGeometryViewModel pos in MicPositions)
                {
                    if (_customInputMode == "Spherical")
                    {
                        pos.ApplyCartesian(); // refresh Az/El/R display from the current X/Y/Z
                    }
                    else
                    {
                        pos.ApplySpherical(); // recompute X/Y/Z from the entered Az/El/R
                    }
                }
                OnPropertyChanged(nameof(IsCustomCartesian));
                OnPropertyChanged(nameof(IsCustomSpherical));
            }
        }
    }

    public bool IsCustomCartesian
    {
        get => _customInputMode == "Cartesian";
        set { if (value) CustomInputMode = "Cartesian"; }
    }

    public bool IsCustomSpherical
    {
        get => _customInputMode == "Spherical";
        set { if (value) CustomInputMode = "Spherical"; }
    }

    /// <summary>True while a replay file is loaded — locks <see cref="MicCount"/> to the file's channel count.</summary>
    public bool MicCountLocked
    {
        get => _micCountLocked;
        private set { if (SetProperty(ref _micCountLocked, value)) OnPropertyChanged(nameof(CanEditMicCount)); }
    }

    public bool CanEditMicCount => CanEditConfig && !_micCountLocked;

    /// <summary>True when the current mic positions are collinear (all Y equal), which makes a
    /// source and its mirror image across the array line indistinguishable (front/back ambiguity).</summary>
    public bool HasFrontBackAmbiguity
    {
        get => _hasFrontBackAmbiguity;
        private set
        {
            if (SetProperty(ref _hasFrontBackAmbiguity, value))
            {
                OnPropertyChanged(nameof(MinLocalizationPairs));
                OnPropertyChanged(nameof(LocalizationToolRequirementsText));
            }
        }
    }

    /// <summary>Fewest channel pairs SRP-PHAT needs to produce an azimuth. Normally 2 (two baselines
    /// resolve the front/back mirror ambiguity); relaxed to 1 for a collinear array, where
    /// <see cref="GccPhat.Core.SrpPhatLocalizer"/> already restricts the search to the front
    /// hemisphere and a single pair is enough for that ambiguous-but-usable estimate.</summary>
    public int MinLocalizationPairs => HasFrontBackAmbiguity ? 1 : 2;

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

    /// <summary>
    /// Activity gate (dBFS, loudest channel): delay estimation, localization, beamforming, and
    /// classification all pause while the loudest channel is below this level.
    /// </summary>
    public double LevelThresholdDb
    {
        get => _levelThresholdDb;
        set
        {
            if (SetProperty(ref _levelThresholdDb, value))
            {
                _hasLiveAzimuth = false;
                Engine.SetLevelThresholdDb(value);
                OnPropertyChanged(nameof(IsAboveThreshold));
                OnPropertyChanged(nameof(HasVisibleLocalizationAzimuth));
                OnPropertyChanged(nameof(GateStatusText));
            }
        }
    }

    /// <summary>Loudest channel level (dBFS) of the latest frame; drives the compass gate.</summary>
    public double CurrentLevelDb
    {
        get => _currentLevelDb;
        private set
        {
            if (SetProperty(ref _currentLevelDb, value))
            {
                if (_currentLevelDb < _levelThresholdDb)
                {
                    _hasLiveAzimuth = false;
                }
                OnPropertyChanged(nameof(LevelText));
                OnPropertyChanged(nameof(IsAboveThreshold));
                OnPropertyChanged(nameof(HasVisibleLocalizationAzimuth));
                OnPropertyChanged(nameof(GateStatusText));
            }
        }
    }

    /// <summary>Human-readable state of the activity gate, for display next to the threshold slider.</summary>
    public string GateStatusText => IsAboveThreshold ? "● Gate open — processing" : "○ Gate closed — waiting for sound";

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
                UpdateBeamPattern();
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
            return $"{mode} Processing block: {SelectedBufferSize} samples (~{blockMs:F1} ms at {sampleRate} Hz), using the Analysis window. Estimates update once per window.";
        }
    }

    /// <summary>Analysis window duration in milliseconds, from the sample rate of whichever source
    /// (live device or replay file) is currently selected.</summary>
    public string WindowDurationText
    {
        get
        {
            int sampleRate = IsFileMode ? _replaySampleRate : (SelectedDevice?.SampleRate ?? 0);
            if (sampleRate <= 0)
            {
                return "Window duration: select a device or file first.";
            }
            double ms = SelectedBufferSize * 1000.0 / sampleRate;
            return $"Window duration: {ms:F1} ms @ {sampleRate} Hz";
        }
    }

    public string DelayToolRequirementsText => "Needs: at least 1 channel pair, then START.";

    public string DelayToolStatusText
        => ActivePairs.Count < 1
            ? "Missing: add at least 1 channel pair."
            : IsRunning
                ? "Ready now."
                : "Ready after you press START.";

    public string LocalizationToolRequirementsText
        => $"Needs: geometry mapped to unique capture channels, at least {MinLocalizationPairs} mapped channel pair{Pluralize(MinLocalizationPairs)}, then START.";

    public string LocalizationToolStatusText
    {
        get
        {
            if (!HasValidSpatialGeometry)
            {
                return $"Missing: {GeometryIssueText}.";
            }

            int eligiblePairs = GetEligibleLocalizationPairCount();
            int minPairs = MinLocalizationPairs;
            if (eligiblePairs < minPairs)
            {
                int missing = minPairs - eligiblePairs;
                return $"Missing: add {missing} more mapped channel pair{Pluralize(missing)}.";
            }

            return IsRunning ? "Ready now." : "Ready after you press START.";
        }
    }

    public bool CanLocalizeWithCurrentPairs => HasValidSpatialGeometry && GetEligibleLocalizationPairCount() >= MinLocalizationPairs;
    public bool HasVisibleLocalizationAzimuth => IsRunning && IsAboveThreshold && CanLocalizeWithCurrentPairs && _hasLiveAzimuth;

    /// <summary>True while the currently configured pairs are exactly the ones <see cref="TryAutoApplyDefaultLocalizationPairs"/> seeded — cleared as soon as the user adds/removes a pair.</summary>
    public bool LocalizationPairsAutoApplied
    {
        get => _localizationPairsAutoApplied;
        private set => SetProperty(ref _localizationPairsAutoApplied, value);
    }

    /// <summary>
    /// Called when the array map ("Localization") window opens: if no channel pairs are configured
    /// yet, seeds them with the opposite-pair preset so localization works without any manual setup.
    /// Never overrides pairs the user has already configured.
    /// </summary>
    public void TryAutoApplyDefaultLocalizationPairs()
    {
        if (ActivePairs.Count > 0)
        {
            return;
        }

        AddOppositePairs();
        if (ActivePairs.Count > 0)
        {
            LocalizationPairsAutoApplied = true;
        }
    }

    public string LocalizationPairSummaryText
    {
        get
        {
            int totalPairs = ActivePairs.Count;
            if (totalPairs == 0)
            {
                return "No channel pairs configured yet.";
            }

            int eligiblePairs = GetEligibleLocalizationPairCount();
            int usedPairs = GetUsedLocalizationPairCount(eligiblePairs);
            return $"{usedPairs} of {totalPairs} pair{Pluralize(totalPairs)} currently used for SRP-PHAT.";
        }
    }

    public string LocalizationPairHintText
    {
        get
        {
            int minPairs = MinLocalizationPairs;
            if (ActivePairs.Count == 0)
            {
                return $"Add at least {minPairs} mapped pair{Pluralize(minPairs)} to start localization.";
            }

            int eligiblePairs = GetEligibleLocalizationPairCount();
            if (!HasValidSpatialGeometry)
            {
                return $"Finish geometry setup first: {GeometryIssueText}.";
            }

            if (eligiblePairs < minPairs)
            {
                int missingPairs = minPairs - eligiblePairs;
                return $"SRP-PHAT starts when at least {minPairs} pair{Pluralize(minPairs)} are ready ({missingPairs} more needed).";
            }

            int ignoredPairs = ActivePairs.Count - eligiblePairs;
            return ignoredPairs == 0
                ? "All configured pairs are being used."
                : $"{ignoredPairs} pair{Pluralize(ignoredPairs)} are ignored until both channels are mapped in Geometry.";
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
    public RelayCommand AddOppositePairsCommand { get; }
    public RelayCommand AddConsecutivePairsCommand { get; }
    public RelayCommand AddAllPairsCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ListenCommand { get; }
    public RelayCommand LoadPresetCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand SaveAsPresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }

    /// <summary>Name of the preset selected in the presets list, used by Load/Save/Delete.</summary>
    public string? SelectedPresetName
    {
        get => _selectedPresetName;
        set
        {
            if (SetProperty(ref _selectedPresetName, value))
            {
                LoadPresetCommand.RaiseCanExecuteChanged();
                SavePresetCommand.RaiseCanExecuteChanged();
                DeletePresetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Name typed for saving a new preset (Save As always uses this).</summary>
    public string NewPresetName
    {
        get => _newPresetName;
        set
        {
            if (SetProperty(ref _newPresetName, value))
            {
                SavePresetCommand.RaiseCanExecuteChanged();
                SaveAsPresetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AudioDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                UpdateDeviceClaim();
                RebuildChannelList();
                OnPropertyChanged(nameof(BeamProcessingText));
                OnPropertyChanged(nameof(WindowDurationText));
                OnPropertyChanged(nameof(SessionLabel));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>Human-readable label for this session, used by the combined-localization window's session pickers.</summary>
    public string SessionLabel => SelectedDevice?.Name ?? "(no device selected)";

    /// <summary>"Live" (a capture device) or "File" (replaying a WAV file).</summary>
    public string CaptureMode
    {
        get => _captureMode;
        set
        {
            if (SetProperty(ref _captureMode, value))
            {
                if (_captureMode == "Live")
                {
                    MicCountLocked = false;
                }
                OnPropertyChanged(nameof(IsLiveMode));
                OnPropertyChanged(nameof(IsFileMode));
                OnPropertyChanged(nameof(WindowDurationText));
                RaiseCommandStates();
            }
        }
    }

    public bool IsLiveMode
    {
        get => _captureMode == "Live";
        set { if (value) CaptureMode = "Live"; }
    }

    public bool IsFileMode
    {
        get => _captureMode == "File";
        set { if (value) CaptureMode = "File"; }
    }

    public string? ReplayFilePath
    {
        get => _replayFilePath;
        private set
        {
            if (SetProperty(ref _replayFilePath, value))
            {
                OnPropertyChanged(nameof(ReplayFileLabel));
                RaiseCommandStates();
            }
        }
    }

    public string ReplayFileLabel => ReplayFilePath is null ? "No file selected." : Path.GetFileName(ReplayFilePath);

    public string ReplayChannelsDetectedText
    {
        get => _replayChannelsDetectedText;
        private set => SetProperty(ref _replayChannelsDetectedText, value);
    }

    /// <summary>Raised when a replay reaches end-of-file on its own (not a user-initiated Stop). The
    /// subscriber (MainWindow) must marshal to the UI thread and call Stop().</summary>
    public event EventHandler? ReplayFinished;

    /// <summary>Called from code-behind after the user picks a WAV file via the file dialog.</summary>
    public void LoadReplayFile(string path)
    {
        try
        {
            using var probe = new WaveFileReader(path);
            int channels = probe.WaveFormat.Channels;
            _replaySampleRate = probe.WaveFormat.SampleRate;

            ReplayFilePath = path;
            ReplayChannelsDetectedText = $"Channels detected: {channels}";
            SelectedLayout = "Custom";
            MicCount = channels;
            MicCountLocked = true;
            OnPropertyChanged(nameof(WindowDurationText));
            StatusText = $"Loaded \"{Path.GetFileName(path)}\" — {channels} channel(s). "
                       + $"Set {channels} microphone position(s) in GEOMETRY, then START.";
        }
        catch (Exception ex)
        {
            ReplayFilePath = null;
            ReplayChannelsDetectedText = "Channels detected: —";
            _replaySampleRate = 0;
            OnPropertyChanged(nameof(WindowDurationText));
            StatusText = $"Failed to read WAV file: {ex.Message}";
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
                OnPropertyChanged(nameof(WindowDurationText));
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
                OnPropertyChanged(nameof(CanEditMicCount));
                OnPropertyChanged(nameof(BeamWorkflowText));
                OnPropertyChanged(nameof(HasVisibleLocalizationAzimuth));
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
            int hiddenCount = 0;
            foreach (AudioDeviceInfo device in DeviceEnumerator.ListCaptureDevices())
            {
                if (s_claimedDevices.TryGetValue(device.Id, out MainViewModel? owner) && owner != this)
                {
                    hiddenCount++;
                    continue; // already selected in another open analysis window
                }
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

            StatusText = $"Found {Devices.Count} capture device(s) and {RenderDevices.Count} render device(s)."
                       + (hiddenCount > 0 ? $" ({hiddenCount} hidden — already selected in another window.)" : string.Empty);
            NotifyToolStateChanged();
        }
        catch (Exception ex)
        {
            StatusText = $"Device enumeration failed: {ex.Message}";
        }
    }

    // Keeps s_claimedDevices in sync with this instance's current selection (by device Id, not
    // object reference, since RefreshDevices() re-enumerates fresh AudioDeviceInfo instances).
    private void UpdateDeviceClaim()
    {
        string? newId = _selectedDevice?.Id;
        if (newId == _claimedDeviceId)
        {
            return;
        }

        ReleaseDeviceClaim();
        _claimedDeviceId = newId;
        if (newId is not null)
        {
            s_claimedDevices[newId] = this;
        }
    }

    /// <summary>Releases this window's device claim (call when its window closes).</summary>
    public void ReleaseDeviceClaim()
    {
        if (_claimedDeviceId is not null)
        {
            s_claimedDevices.Remove(_claimedDeviceId);
            _claimedDeviceId = null;
        }
    }

    /// <summary>Call when this session's window closes: releases its device claim and drops it from the open-session registry.</summary>
    public void Shutdown()
    {
        ReleaseDeviceClaim();
        s_openSessions.Remove(this);
        OpenSessionsChanged?.Invoke();
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
           && a != b
           && !ActivePairs.Any(p => p.Pair.UnorderedKey == new ChannelPair(a, b).UnorderedKey);

    private void AddPair()
    {
        if (SelectedChannelA is not int a || SelectedChannelB is not int b || a == b)
        {
            return;
        }

        var pair = new ChannelPair(a, b);
        if (ActivePairs.Any(p => p.Pair.UnorderedKey == pair.UnorderedKey))
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
        ReindexActivePairs();
        SelectedPair = null;
        SyncEnginePairs();
        RaiseCommandStates();
    }

    // Reindexes ActivePairs so palette colours stay contiguous after any removal.
    private void ReindexActivePairs()
    {
        var snapshot = ActivePairs.ToList();
        ActivePairs.Clear();
        for (int i = 0; i < snapshot.Count; i++)
        {
            ActivePairs.Add(new PairViewModel(snapshot[i].Pair, i));
        }
    }

    private void SyncEnginePairs() => Engine.SetPairs(ActivePairs.Select(p => p.Pair));

    /// <summary>Toggles one pair per diametrically opposite ring microphone (circular layout, even mic count).</summary>
    private void AddOppositePairs()
    {
        if (!IsCircular || _micCount % 2 != 0)
        {
            StatusText = "Opposite pairs need a circular layout with an even mic count.";
            return;
        }

        var byPosition = MicPositions
            .Where(p => p.PositionIndex < _micCount)
            .ToDictionary(p => p.PositionIndex);
        int half = _micCount / 2;
        var pairs = new List<(int A, int B)>(half);
        for (int i = 0; i < half; i++)
        {
            if (byPosition.TryGetValue(i, out MicGeometryViewModel? a) && byPosition.TryGetValue(i + half, out MicGeometryViewModel? b)
                && a.Channel is int ca && b.Channel is int cb)
            {
                pairs.Add((ca, cb));
            }
        }
        ToggleQuickAddPairs(pairs, "opposite");
    }

    /// <summary>Toggles a pair between each microphone and its immediate neighbor (ring wraps for circular).</summary>
    private void AddConsecutivePairs()
    {
        var ring = MicPositions.Where(p => p.PositionIndex < _micCount).ToDictionary(p => p.PositionIndex);
        int n = ring.Count;
        int steps = IsCircular ? n : n - 1;
        var pairs = new List<(int A, int B)>(steps);
        for (int i = 0; i < steps; i++)
        {
            int j = (i + 1) % n;
            if (ring.TryGetValue(i, out MicGeometryViewModel? a) && ring.TryGetValue(j, out MicGeometryViewModel? b)
                && a.Channel is int ca && b.Channel is int cb)
            {
                pairs.Add((ca, cb));
            }
        }
        ToggleQuickAddPairs(pairs, "consecutive");
    }

    /// <summary>Toggles every possible pair among the currently assigned microphones (center mic included).</summary>
    private void AddAllPairs()
    {
        var channels = MicPositions.Where(p => p.Channel is int).Select(p => p.Channel!.Value).Distinct().ToList();
        var pairs = new List<(int A, int B)>(channels.Count * (channels.Count - 1) / 2);
        for (int i = 0; i < channels.Count; i++)
        {
            for (int j = i + 1; j < channels.Count; j++)
            {
                pairs.Add((channels[i], channels[j]));
            }
        }
        ToggleQuickAddPairs(pairs, "possible");
    }

    /// <summary>
    /// If every one of the given channel pairs is already active, removes them all (deselect).
    /// Otherwise adds whichever ones are missing (select).
    /// </summary>
    private void ToggleQuickAddPairs(List<(int A, int B)> channelPairs, string kindLabel)
    {
        List<(int A, int B)> keys = channelPairs
            .Where(p => p.A != p.B)
            .Select(p => new ChannelPair(p.A, p.B).UnorderedKey)
            .Distinct()
            .ToList();

        if (keys.Count == 0)
        {
            StatusText = $"No {kindLabel} pairs available (assign mic channels first).";
            return;
        }

        bool allPresent = keys.All(key => ActivePairs.Any(p => p.Pair.UnorderedKey == key));
        if (allPresent)
        {
            RemovePairsByKeys(keys);
            StatusText = $"Removed {keys.Count} {kindLabel} pair{Pluralize(keys.Count)}.";
            return;
        }

        int added = 0;
        foreach ((int a, int b) in channelPairs)
        {
            if (a == b)
            {
                continue;
            }
            var pair = new ChannelPair(a, b);
            if (ActivePairs.Any(p => p.Pair.UnorderedKey == pair.UnorderedKey))
            {
                continue;
            }
            ActivePairs.Add(new PairViewModel(pair, ActivePairs.Count));
            added++;
        }

        SyncEnginePairs();
        RaiseCommandStates();
        StatusText = $"Added {added} {kindLabel} pair{Pluralize(added)}.";
    }

    private void RemovePairsByKeys(List<(int A, int B)> keys)
    {
        var toRemove = ActivePairs.Where(p => keys.Contains(p.Pair.UnorderedKey)).ToList();
        if (toRemove.Count == 0)
        {
            return;
        }
        foreach (PairViewModel vm in toRemove)
        {
            ActivePairs.Remove(vm);
        }
        ReindexActivePairs();
        SelectedPair = null;
        SyncEnginePairs();
        RaiseCommandStates();
    }

    /// <summary>Sets the default 4-mic linear array (4 cm spacing), broadside to the user.</summary>
    private void ApplyLinearBroadsidePreset()
    {
        SelectedLayout = "Linear";
        MicCount = 4;
        SpacingCm = 4;
    }

    /// <summary>Sets a 6-mic circular array (8 cm diameter) with an additional center mic.</summary>
    private void ApplyCircular6CenterPreset()
    {
        SelectedLayout = "Circular";
        MicCount = 6;
        DiameterCm = 8;
        HasCenterMic = true;
    }

    private void RefreshPresetNames()
    {
        PresetNames.Clear();
        foreach (string name in _presetStore.ListPresetNames())
        {
            PresetNames.Add(name);
        }
    }

    private void LoadPreset()
    {
        if (SelectedPresetName is null)
        {
            return;
        }
        ArrayGeometryPreset? preset = _presetStore.Load(SelectedPresetName);
        if (preset is null)
        {
            StatusText = $"Failed to load preset \"{SelectedPresetName}\".";
            return;
        }

        DetachGeometryPositionHandlers();

        _diameterCm = preset.DiameterCm;
        _spacingCm = preset.SpacingCm;
        _hasCenterMic = preset.HasCenterMic;
        _customInputMode = preset.CustomInputMode;
        _micCount = preset.MicCount;
        _selectedLayout = preset.Layout;
        OnPropertyChanged(nameof(DiameterCm));
        OnPropertyChanged(nameof(SpacingCm));
        OnPropertyChanged(nameof(HasCenterMic));
        OnPropertyChanged(nameof(CustomInputMode));
        OnPropertyChanged(nameof(IsCustomCartesian));
        OnPropertyChanged(nameof(IsCustomSpherical));
        OnPropertyChanged(nameof(MicCount));
        OnPropertyChanged(nameof(SelectedLayout));
        OnPropertyChanged(nameof(IsCircular));
        OnPropertyChanged(nameof(IsLinear));
        OnPropertyChanged(nameof(IsCustom));

        MicPositions.Clear();
        foreach (MicPositionEntry entry in preset.Positions)
        {
            var pos = new MicGeometryViewModel(entry.PositionIndex, entry.X, entry.Y, AvailableChannels, z: entry.Z)
            {
                Channel = entry.Channel,
                IncludeInBeam = entry.IncludeInBeam
            };
            MicPositions.Add(pos);
        }

        HasFrontBackAmbiguity = MicPositions.Count >= 2 && MicPositions.Max(p => p.Y) - MicPositions.Min(p => p.Y) < 1e-9;
        AttachGeometryPositionHandlers();
        SyncBeamChannels();
        UpdateBeamBand();
        UpdateBeamPattern();
        UpdateDelayAxisRange();
        NotifyToolStateChanged();
        StatusText = $"Loaded preset \"{preset.Name}\".";
    }

    private void SavePreset() => SaveGeometryPreset(SelectedPresetName ?? NewPresetName);

    private void SaveAsPreset() => SaveGeometryPreset(NewPresetName);

    private void SaveGeometryPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText = "Enter a preset name first.";
            return;
        }

        var preset = new ArrayGeometryPreset(
            name,
            SelectedLayout,
            MicCount,
            DiameterCm,
            SpacingCm,
            HasCenterMic,
            CustomInputMode,
            MicPositions.Select(p => new MicPositionEntry(p.PositionIndex, p.X, p.Y, p.Z, p.Channel, p.IncludeInBeam)).ToList());

        _presetStore.Save(preset);
        RefreshPresetNames();
        SelectedPresetName = name;
        NewPresetName = "";
        StatusText = $"Saved preset \"{name}\".";
    }

    private void DeletePreset()
    {
        if (SelectedPresetName is null)
        {
            return;
        }
        string name = SelectedPresetName;
        _presetStore.Delete(name);
        RefreshPresetNames();
        SelectedPresetName = null;
        StatusText = $"Deleted preset \"{name}\".";
    }

    // Recomputes mic positions (metres) from the selected array layout and its parameters.
    private void RebuildPositions()
    {
        DetachGeometryPositionHandlers();
        int n = Math.Clamp(_micCount, 2, 64);
        if (IsCustom)
        {
            // User-entered positions — resize to n entries without discarding existing coordinates.
            while (MicPositions.Count > n)
            {
                MicPositions.RemoveAt(MicPositions.Count - 1);
            }
            while (MicPositions.Count < n)
            {
                MicPositions.Add(new MicGeometryViewModel(MicPositions.Count, 0.0, 0.0, AvailableChannels));
            }
        }
        else
        {
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
        }
        HasFrontBackAmbiguity = MicPositions.Count >= 2 && MicPositions.Max(p => p.Y) - MicPositions.Min(p => p.Y) < 1e-9;
        AttachGeometryPositionHandlers();
        UpdateBeamBand();
        UpdateBeamPattern();
        UpdateDelayAxisRange();
        NotifyToolStateChanged();
    }

    // Builds per-channel geometry arrays (metres) from the channel→position assignments.
    private void SyncEngineGeometry()
    {
        if (!HasValidSpatialGeometry)
        {
            Engine.SetGeometry(Array.Empty<double>(), Array.Empty<double>());
            return;
        }

        int maxChannel = MicPositions.Where(p => p.Channel is int).Select(p => p.Channel!.Value).DefaultIfEmpty(-1).Max();
        if (maxChannel < 0)
        {
            Engine.SetGeometry(Array.Empty<double>(), Array.Empty<double>());
            return;
        }
        var x = Enumerable.Repeat(double.NaN, maxChannel + 1).ToArray();
        var y = Enumerable.Repeat(double.NaN, maxChannel + 1).ToArray();
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

    /// <summary>
    /// Sets the default delay-view Y-axis range to 1.1x the maximum possible propagation delay
    /// (biggest pair aperture in the array / speed of sound), so the plot's manual range always
    /// starts sized to what the geometry can actually produce. Recomputed on every geometry change;
    /// only affects the manual min/max used while Auto-scale is unchecked.
    /// </summary>
    private void UpdateDelayAxisRange()
    {
        const double speed = 343.0; // m/s

        if (MicPositions.Count < 2)
        {
            return;
        }

        double dMax = 0.0;
        for (int i = 0; i < MicPositions.Count; i++)
        {
            for (int j = i + 1; j < MicPositions.Count; j++)
            {
                double dx = MicPositions[i].X - MicPositions[j].X;
                double dy = MicPositions[i].Y - MicPositions[j].Y;
                double dz = MicPositions[i].Z - MicPositions[j].Z;
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dist > dMax)
                {
                    dMax = dist;
                }
            }
        }

        if (dMax <= 0)
        {
            return;
        }

        double maxDelayMs = dMax / speed * 1000.0;
        YMax = Math.Round(maxDelayMs * 1.1, 2);
        YMin = -YMax;
    }

    /// <summary>
    /// Recomputes the theoretical polar response (dB) of the current beam design at
    /// <see cref="PatternFrequencyHz"/>, for display alongside the azimuth preview.
    /// </summary>
    private void UpdateBeamPattern()
    {
        var selected = MicPositions
            .Where(p => p.Channel is int && p.IncludeInBeam)
            .ToList();
        if (selected.Count == 0)
        {
            BeamPolarPattern = null;
            OnPropertyChanged(nameof(BeamPolarPattern));
            return;
        }

        var x = selected.Select(p => p.X).ToArray();
        var y = selected.Select(p => p.Y).ToArray();
        var delays = new double[selected.Count];
        var weights = new double[selected.Count];

        if (ParseBeamformerMode(_selectedBeamformerMode) == BeamformerMode.DifferentialAuto)
        {
            if (!DifferentialBeamformerDesigner.TryBuildWeights(x, y, BeamAzimuthDeg, weights, out _))
            {
                BeamPolarPattern = null;
                OnPropertyChanged(nameof(BeamPolarPattern));
                return;
            }
            // delays stay zero — the differential design steers purely through weights.
        }
        else
        {
            for (int i = 0; i < selected.Count; i++)
            {
                delays[i] = BeamPatternCalculator.SteeringDelaySeconds(x[i], y[i], BeamAzimuthDeg);
                weights[i] = 1.0 / selected.Count;
            }
        }

        double[] magnitude = BeamPatternCalculator.ComputeArrayFactor(x, y, delays, weights, PatternFrequencyHz, BeamPolarPatternStepDeg);
        const double floorDb = -120.0;
        BeamPolarPattern = magnitude
            .Select(m => Math.Max(20.0 * Math.Log10(Math.Max(m, 1e-6)), floorDb))
            .ToArray();
        OnPropertyChanged(nameof(BeamPolarPattern));
    }

    private double _azimuthDeg;
    private bool _beamListening;
    private double _beamAzimuthDeg;
    private double _beamGainDb;
    private bool _beamBandLimit = true;
    private string _beamBandText = "Full band.";
    private string _selectedBeamformerMode = BeamformerModeDelayAndSum;
    private double _patternFrequencyHz = 1000.0;

    /// <summary>Assumed sample rate (Hz) used only to bound the pattern-frequency slider, for now.</summary>
    public const double PatternAssumedSampleRateHz = 16000.0;
    public const double PatternMinFrequencyHz = 20.0;
    public const double PatternMaxFrequencyHz = PatternAssumedSampleRateHz / 2.0;
    public const double BeamPolarPatternStepDeg = 2.0;

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
                UpdateBeamPattern();
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

    /// <summary>
    /// Frequency (Hz) at which the theoretical polar pattern is evaluated. Range is bounded by
    /// <see cref="PatternMinFrequencyHz"/>/<see cref="PatternMaxFrequencyHz"/> (Nyquist for the
    /// assumed 16 kHz sample rate, for now).
    /// </summary>
    public double PatternFrequencyHz
    {
        get => _patternFrequencyHz;
        set
        {
            double clamped = Math.Clamp(value, PatternMinFrequencyHz, PatternMaxFrequencyHz);
            if (SetProperty(ref _patternFrequencyHz, clamped))
            {
                OnPropertyChanged(nameof(PatternFrequencyText));
                UpdateBeamPattern();
            }
        }
    }

    public string PatternFrequencyText => $"{PatternFrequencyHz:F0} Hz";

    /// <summary>Theoretical polar response (dB, floor-clamped) of the current beam design at <see cref="PatternFrequencyHz"/>; null when no mics are selected.</summary>
    public double[]? BeamPolarPattern { get; private set; }

    // ── Classification ──────────────────────────────────────────────────────────
    public YamNetClassifier Classifier { get; } = new();
    public ClassificationViewModel ClassificationVm { get; } = new();

    private int _classificationChannel;
    public int ClassificationChannel
    {
        get => _classificationChannel;
        set
        {
            if (SetProperty(ref _classificationChannel, value))
                Engine.SetClassifier(Classifier, value);
        }
    }

    public void UpdateClassification(ClassificationResult[] results)
        => ClassificationVm.UpdateResults(results, _classificationChannel);

    // ── SRP-PHAT spectrum ───────────────────────────────────────────────────────
    public double[]? SrpSpectrum { get; private set; }
    public double SrpSpectrumStepDeg { get; private set; }
    public double[,]? HemispherePowers { get; private set; }
    public double HemiElStepDeg { get; private set; }

    private bool _showHemisphere;
    public bool ShowHemisphere
    {
        get => _showHemisphere;
        set
        {
            if (SetProperty(ref _showHemisphere, value))
            {
                Engine.SetHemisphereMode(value);
                if (!value) HemispherePowers = null;
            }
        }
    }

    /// <summary>Called on the UI thread with the latest SRP-PHAT azimuth over the active pairs.</summary>
    public void UpdateAzimuth(SrpEstimate estimate)
    {
        AzimuthDeg = estimate.AzimuthDeg;
        AzimuthText = $"Source azimuth: {estimate.AzimuthDeg,6:F1}\u00b0";
        SrpSpectrum = estimate.CoarsePowers;
        SrpSpectrumStepDeg = estimate.CoarseStepDeg;
        HemispherePowers = estimate.HemispherePowers;
        HemiElStepDeg = estimate.HemiElStepDeg;
        _hasLiveAzimuth = true;
        OnPropertyChanged(nameof(HasVisibleLocalizationAzimuth));
    }

    private int _captureGeneration;

    private void Start(bool isRetry = false, bool forcePortAudio = false)
    {
        if (IsFileMode)
        {
            StartFileReplay();
            return;
        }

        if (SelectedDevice is null)
        {
            return;
        }

        int generation = ++_captureGeneration;

        try
        {
            var capture = new MultichannelCapture(SelectedDevice, forcePortAudio);
            StartWithCapture(capture, $"Running on \"{SelectedDevice.Name}\"");

            if (forcePortAudio)
            {
                StatusText = "(WDM-KS) " + StatusText;
            }
            else
            {
                ProbeForSilence(capture, generation);
            }
        }
        catch (Exception ex)
        {
            IsRunning = false;

            if (!isRetry && SelectedDevice.UseExclusive && TryOfferToCloseHolders())
            {
                Start(isRetry: true);
                return;
            }

            string hint = SelectedDevice.UseExclusive
                ? " This device needs exclusive mode for all channels; close any app using it and retry."
                : string.Empty;
            StatusText = $"Failed to start capture: {ex.Message}.{hint}";
        }
    }

    private void StartFileReplay()
    {
        if (ReplayFilePath is null)
        {
            StatusText = "Choose a WAV file first.";
            return;
        }

        WavFileReplayCapture? capture = null;
        try
        {
            capture = new WavFileReplayCapture(ReplayFilePath);
            if (capture.ChannelCount != MicPositions.Count)
            {
                StatusText = $"File has {capture.ChannelCount} channel(s) but geometry has {MicPositions.Count} position(s) - mismatch.";
                capture.Dispose();
                return;
            }

            _activeReplay = capture;
            capture.PlaybackFinished += OnReplayPlaybackFinished;
            StartWithCapture(capture, $"Replaying \"{Path.GetFileName(ReplayFilePath)}\"");
        }
        catch (Exception ex)
        {
            capture?.Dispose();
            IsRunning = false;
            StatusText = $"Failed to start replay: {ex.Message}";
        }
    }

    // Shared Start() tail for both live capture and file replay - both are ICaptureSource, so the
    // engine configuration/wiring is identical regardless of where the audio comes from.
    private void StartWithCapture(ICaptureSource capture, string statusPrefix)
    {
        int nyquist = capture.SampleRate / 2;
        int fmax = Math.Min(Fmax, nyquist);
        if (fmax != Fmax)
        {
            Fmax = fmax;
        }

        Engine.Configure(SelectedBufferSize, Fmin, fmax);
        SyncEnginePairs();
        SyncEngineGeometry();
        SyncBeamChannels();
        Engine.Start(capture);

        IsRunning = true;
        StatusText = $"{statusPrefix} - {capture.ChannelCount} ch @ {capture.SampleRate} Hz, "
                   + $"window {SelectedBufferSize}, band {Fmin}-{fmax} Hz.";
    }

    // Raised on the replay pump thread when the file runs out on its own. MainWindow marshals to
    // the UI thread and calls Stop() - never call Stop() directly from here.
    private void OnReplayPlaybackFinished()
    {
        _replayEndedNaturally = true;
        ReplayFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Some mic-array drivers install a voice-processing APO that silences the WASAPI stream
    /// outright. ~500 ms after a successful WASAPI start, checks whether every channel is still at
    /// the noise floor and, if so, restarts through the WDM-KS backend instead (bypasses the APO).
    /// </summary>
    private void ProbeForSilence(MultichannelCapture capture, int generation)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (generation != _captureGeneration || !IsRunning)
            {
                return; // stopped or restarted (by the user or another probe) since this was scheduled
            }
            if (IsCaptureSilent(capture))
            {
                Stop();
                StatusText = "WASAPI came back silent - switched to WDM-KS raw capture.";
                Start(forcePortAudio: true);
            }
        };
        timer.Start();
    }

    private static bool IsCaptureSilent(MultichannelCapture capture)
    {
        const double thresholdLinear = 1e-4; // ~ -80 dBFS
        var buf = new double[256];
        for (int c = 0; c < capture.ChannelCount; c++)
        {
            if (!capture.GetChannel(c).CopyLatest(buf))
            {
                return false; // not enough data yet to judge; don't fall back on insufficient data
            }
            for (int i = 0; i < buf.Length; i++)
            {
                if (Math.Abs(buf[i]) > thresholdLinear)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Looks up other processes with an active session on the selected device and, if the user
    /// agrees, force-closes them. Returns true if the user confirmed and at least one was closed.
    /// </summary>
    private bool TryOfferToCloseHolders()
    {
        var holders = AudioSessionInspector.FindActiveHolders(SelectedDevice!.Device);
        if (holders.Count == 0)
        {
            return false;
        }

        string names = string.Join(", ", holders.Select(h => $"{h.ProcessName} (PID {h.ProcessId})"));
        var result = System.Windows.MessageBox.Show(
            $"These apps appear to be using \"{SelectedDevice.Name}\":\n{names}\n\nClose them and retry?",
            "Microphone in use",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return false;
        }

        var failed = AudioSessionInspector.KillAll(holders);
        if (failed.Count > 0)
        {
            string failedNames = string.Join(", ", failed.Select(h => h.ProcessName));
            System.Windows.MessageBox.Show(
                $"Could not close: {failedNames}. Close them manually and retry.",
                "Microphone in use",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }

        System.Threading.Thread.Sleep(300); // let the audio engine release the endpoint
        return failed.Count < holders.Count;
    }

    private void Stop()
    {
        Engine.Stop();
        if (_activeReplay is not null)
        {
            _activeReplay.PlaybackFinished -= OnReplayPlaybackFinished;
            _activeReplay = null;
        }
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
        SrpSpectrum = null;
        HemispherePowers = null;
        _hasLiveAzimuth = false;
        OnPropertyChanged(nameof(HasVisibleLocalizationAzimuth));
        StatusText = _replayEndedNaturally ? "Replay finished." : "Stopped.";
        _replayEndedNaturally = false;
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

    private void RefreshLocalizationPairStates()
    {
        HashSet<int> availableChannels = AvailableChannels.ToHashSet();
        HashSet<int> mappedChannels = GetMappedGeometryChannels(availableChannels);
        int eligiblePairs = GetEligibleLocalizationPairCount(availableChannels, mappedChannels);
        int minPairs = MinLocalizationPairs;
        int missingPairs = Math.Max(0, minPairs - eligiblePairs);
        var seenBaselines = new Dictionary<(int A, int B), string>();

        foreach (PairViewModel pair in ActivePairs)
        {
            bool hasChannelA = availableChannels.Contains(pair.Pair.ChannelA);
            bool hasChannelB = availableChannels.Contains(pair.Pair.ChannelB);
            if (!hasChannelA || !hasChannelB)
            {
                string missing = FormatChannelList(GetMissingPairChannels(pair.Pair, ch => availableChannels.Contains(ch)));
                pair.SetLocalizationState(LocalizationPairState.Ignored,
                    $"Ignored: {missing} not available on the selected capture device.");
                continue;
            }

            bool mappedA = mappedChannels.Contains(pair.Pair.ChannelA);
            bool mappedB = mappedChannels.Contains(pair.Pair.ChannelB);
            if (!mappedA || !mappedB)
            {
                string missing = FormatChannelList(GetMissingPairChannels(pair.Pair, ch => mappedChannels.Contains(ch)));
                pair.SetLocalizationState(LocalizationPairState.Ignored,
                    $"Ignored: map {missing} in Geometry.");
                continue;
            }

            if (seenBaselines.TryGetValue(pair.Pair.UnorderedKey, out string? firstPairLabel))
            {
                pair.SetLocalizationState(LocalizationPairState.Ignored,
                    $"Ignored: same baseline already covered by {firstPairLabel ?? "the earlier pair"}.");
                continue;
            }

            seenBaselines[pair.Pair.UnorderedKey] = pair.Label;

            if (!HasValidSpatialGeometry)
            {
                pair.SetLocalizationState(LocalizationPairState.Waiting,
                    $"Waiting: {GeometryIssueText}.");
                continue;
            }

            if (eligiblePairs < minPairs)
            {
                pair.SetLocalizationState(LocalizationPairState.Waiting,
                    missingPairs == 1
                        ? "Waiting: add 1 more mapped pair."
                        : $"Waiting: add {missingPairs} more mapped pairs.");
                continue;
            }

            pair.SetLocalizationState(LocalizationPairState.Used, "Used by SRP-PHAT.");
        }
    }

    private int GetEligibleLocalizationPairCount()
    {
        HashSet<int> availableChannels = AvailableChannels.ToHashSet();
        HashSet<int> mappedChannels = GetMappedGeometryChannels(availableChannels);
        return GetEligibleLocalizationPairCount(availableChannels, mappedChannels);
    }

    private int GetEligibleLocalizationPairCount(HashSet<int> availableChannels, HashSet<int> mappedChannels)
        => ActivePairs
            .Select(pair => pair.Pair)
            .Where(pair => IsPairMappedForLocalization(pair, availableChannels, mappedChannels))
            .Select(pair => pair.UnorderedKey)
            .Distinct()
            .Count();

    private int GetUsedLocalizationPairCount(int eligiblePairs)
        => HasValidSpatialGeometry && eligiblePairs >= MinLocalizationPairs
            ? eligiblePairs
            : 0;

    private HashSet<int> GetMappedGeometryChannels(HashSet<int> availableChannels)
        => MicPositions
            .Where(p => p.Channel is int ch && availableChannels.Contains(ch))
            .Select(p => p.Channel!.Value)
            .ToHashSet();

    private static bool IsPairMappedForLocalization(ChannelPair pair, HashSet<int> availableChannels, HashSet<int> mappedChannels)
        => availableChannels.Contains(pair.ChannelA)
           && availableChannels.Contains(pair.ChannelB)
           && mappedChannels.Contains(pair.ChannelA)
           && mappedChannels.Contains(pair.ChannelB);

    private static IEnumerable<int> GetMissingPairChannels(ChannelPair pair, Func<int, bool> isPresent)
    {
        if (!isPresent(pair.ChannelA))
        {
            yield return pair.ChannelA;
        }

        if (!isPresent(pair.ChannelB))
        {
            yield return pair.ChannelB;
        }
    }

    private static string FormatChannelList(IEnumerable<int> channels)
        => string.Join(", ", channels.Distinct().Select(ch => $"Ch{ch}"));

    private static string Pluralize(int count) => count == 1 ? string.Empty : "s";

    private void OnActivePairsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        LocalizationPairsAutoApplied = false;
        NotifyToolStateChanged();
    }

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
            UpdateBeamPattern();
        }
        else if (e.PropertyName == nameof(MicGeometryViewModel.IncludeInBeam))
        {
            SyncBeamChannels();
            OnPropertyChanged(nameof(BeamModeStatusText));
            UpdateBeamPattern();
        }
        else if (e.PropertyName is nameof(MicGeometryViewModel.X) or nameof(MicGeometryViewModel.Y) or nameof(MicGeometryViewModel.Z))
        {
            HasFrontBackAmbiguity = MicPositions.Count >= 2 && MicPositions.Max(p => p.Y) - MicPositions.Min(p => p.Y) < 1e-9;
            UpdateBeamPattern();
            UpdateDelayAxisRange();
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
        RefreshLocalizationPairStates();
        if (!CanLocalizeWithCurrentPairs)
        {
            _hasLiveAzimuth = false;
        }
        OnPropertyChanged(nameof(DelayToolStatusText));
        OnPropertyChanged(nameof(CanLocalizeWithCurrentPairs));
        OnPropertyChanged(nameof(HasVisibleLocalizationAzimuth));
        OnPropertyChanged(nameof(LocalizationToolStatusText));
        OnPropertyChanged(nameof(LocalizationPairSummaryText));
        OnPropertyChanged(nameof(LocalizationPairHintText));
        OnPropertyChanged(nameof(BeamformerToolStatusText));
        OnPropertyChanged(nameof(BeamWorkflowText));
        OnPropertyChanged(nameof(BeamModeStatusText));
    }

    private static BeamformerMode ParseBeamformerMode(string mode)
        => mode == BeamformerModeDifferentialAuto
            ? BeamformerMode.DifferentialAuto
            : BeamformerMode.DelayAndSum;
}
