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
    private bool _isRunning;
    private string _statusText = "Select a capture device, add channel pairs, then Start.";
    private PairViewModel? _selectedPair;

    public MainViewModel()
    {
        Engine = new RealTimeEngine();

        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        AddPairCommand = new RelayCommand(AddPair, CanAddPair);
        RemoveSelectedPairCommand = new RelayCommand(RemoveSelectedPair, () => _selectedPair is not null && !IsRunning);
        StartCommand = new RelayCommand(Start, () => !IsRunning && SelectedDevice is not null && ActivePairs.Count > 0);
        StopCommand = new RelayCommand(Stop, () => IsRunning);

        RefreshDevices();
    }

    public RealTimeEngine Engine { get; }

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = new();
    public ObservableCollection<int> AvailableChannels { get; } = new();
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
        int count = SelectedDevice?.ChannelCount ?? 0;
        for (int c = 0; c < count; c++)
        {
            AvailableChannels.Add(c);
        }
        SelectedChannelA = count > 0 ? 0 : null;
        SelectedChannelB = count > 1 ? 1 : (count > 0 ? 0 : null);
    }

    private bool CanAddPair()
        => !IsRunning
           && SelectedChannelA is int a
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
        if (SelectedDevice is null || ActivePairs.Count == 0)
        {
            return;
        }

        try
        {
            var capture = new MultichannelCapture(SelectedDevice.Device);

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
            StatusText = $"Failed to start capture: {ex.Message}";
        }
    }

    private void Stop()
    {
        Engine.Stop();
        IsRunning = false;
        foreach (PairViewModel pair in ActivePairs)
        {
            pair.Valid = false;
        }
        StatusText = "Stopped.";
    }

    /// <summary>Called on the UI thread to refresh per-pair readouts from the latest results.</summary>
    public void UpdateReadouts(IReadOnlyList<PairResult> results)
    {
        foreach (PairResult result in results)
        {
            PairViewModel? vm = ActivePairs.FirstOrDefault(p => p.Pair == result.Pair);
            if (vm is null)
            {
                continue;
            }
            vm.Valid = result.Valid;
            if (result.Valid)
            {
                vm.CurrentDelayMs = result.DelayMs;
                vm.CurrentRms = result.Rms;
            }
        }
    }

    private void RaiseCommandStates()
    {
        AddPairCommand.RaiseCanExecuteChanged();
        RemoveSelectedPairCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }
}
