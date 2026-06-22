using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.Audio;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private AudioDeviceInfo? _selectedDevice;
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

    public MainViewModel()
    {
        Engine = new RealTimeEngine();

        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        AddPairCommand = new RelayCommand(AddPair, CanAddPair);
        RemoveSelectedPairCommand = new RelayCommand(RemoveSelectedPair, () => _selectedPair is not null);
        StartCommand = new RelayCommand(Start, () => !IsRunning && SelectedDevice is not null);
        StopCommand = new RelayCommand(Stop, () => IsRunning);

        RefreshDevices();
    }

    public RealTimeEngine Engine { get; }

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();
    public ObservableCollection<int> AvailableChannels { get; } = new();
    public ObservableCollection<ChannelMeterViewModel> ChannelMeters { get; } = new();
    public ObservableCollection<PairViewModel> ActivePairs { get; } = new();
    public int[] BufferSizeOptions { get; } = { 1024, 2048, 4096, 8192, 16384, 32768 };

    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand AddPairCommand { get; }
    public RelayCommand RemoveSelectedPairCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }

    public AudioDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                RebuildChannelList();
                RaiseCommandStates();
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
        set => SetProperty(ref _selectedBufferSize, value);
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
        set => SetProperty(ref _updateIntervalMs, value);
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
            Devices.Clear();
            foreach (AudioDeviceInfo device in DeviceEnumerator.ListCaptureDevices())
            {
                Devices.Add(device);
            }
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == previousId) ?? Devices.FirstOrDefault();
            StatusText = $"Found {Devices.Count} capture device(s).";
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
    }
}
