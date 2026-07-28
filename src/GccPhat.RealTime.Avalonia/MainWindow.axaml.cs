using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.Audio;
using GccPhat.RealTime.Mvvm;
using GccPhat.RealTime.ViewModels;

namespace GccPhat.RealTime;

/// <summary>Interaction logic for MainWindow.axaml.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IAudioPlatform _platform;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUserPrompt _prompt;

    public MainWindow(IAudioPlatform platform, IUiDispatcher dispatcher, IUserPrompt prompt)
    {
        _platform = platform;
        _dispatcher = dispatcher;
        _prompt = prompt;
        _viewModel = new MainViewModel(platform, dispatcher, prompt);

        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.Engine.ResultsReady += OnResultsReady;
        _viewModel.Engine.ChannelLevelsReady += OnChannelLevels;
        _viewModel.Engine.AzimuthReady += OnAzimuth;
        _viewModel.Engine.ClassificationReady += OnClassificationReady;
        _viewModel.ReplayFinished += OnReplayFinished;
    }

    private async void OnBrowseReplayFile(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select multichannel WAV file",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("WAV audio") { Patterns = new[] { "*.wav" } } }
        });
        if (files.Count > 0)
        {
            _viewModel.LoadReplayFile(files[0].Path.LocalPath);
        }
    }

    // Raised on the replay pump thread when a file finishes playing on its own.
    private void OnReplayFinished(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => _viewModel.StopCommand.Execute(null));

    private void OnChannelLevels(double[] levels)
        => Dispatcher.UIThread.Post(() => _viewModel.UpdateChannelLevels(levels));

    private void OnAzimuth(GccPhat.Core.SrpEstimate estimate)
        => Dispatcher.UIThread.Post(() => _viewModel.UpdateAzimuth(estimate));

    private ArrayMapWindow? _arrayMap;
    private BeamformerWindow? _beamWindow;
    private DelayViewWindow? _delayWindow;
    private LevelViewWindow? _levelWindow;
    private ClassificationWindow? _classificationWindow;

    // Shared across all open MainWindow instances (not owned by any single analysis session).
    private static CombinedLocalizationWindow? s_combinedWindow;

    private void OnShowCombinedLocalization(object? sender, RoutedEventArgs e)
    {
        if (s_combinedWindow is null)
        {
            s_combinedWindow = new CombinedLocalizationWindow(_dispatcher);
            s_combinedWindow.Closed += (_, _) => s_combinedWindow = null;
            s_combinedWindow.Show();
        }
        else
        {
            s_combinedWindow.Activate();
        }
    }

    // Opens a second, fully independent analysis chain (own MainViewModel/RealTimeEngine/device
    // selection) in the same process, so a second microphone array can run concurrently.
    private void OnNewWindow(object? sender, RoutedEventArgs e) => new MainWindow(_platform, _dispatcher, _prompt).Show();

    private void OnShowArrayMap(object? sender, RoutedEventArgs e)
    {
        if (_arrayMap is null)
        {
            _arrayMap = new ArrayMapWindow(_viewModel);
            _arrayMap.Closed += (_, _) => _arrayMap = null;
            _arrayMap.Show();
        }
        else
        {
            _arrayMap.Activate();
        }
    }

    private void OnShowBeamformer(object? sender, RoutedEventArgs e)
    {
        if (_beamWindow is null)
        {
            _beamWindow = new BeamformerWindow(_viewModel);
            _beamWindow.Closed += (_, _) => _beamWindow = null;
            _beamWindow.Show();
        }
        else
        {
            _beamWindow.Activate();
        }
    }

    private void OnShowDelayView(object? sender, RoutedEventArgs e)
    {
        if (_delayWindow is null)
        {
            _delayWindow = new DelayViewWindow(_viewModel);
            _delayWindow.Closed += (_, _) => _delayWindow = null;
            _delayWindow.Show();
        }
        else
        {
            _delayWindow.Activate();
        }
    }

    private void OnShowLevelView(object? sender, RoutedEventArgs e)
    {
        if (_levelWindow is null)
        {
            _levelWindow = new LevelViewWindow(_viewModel);
            _levelWindow.Closed += (_, _) => _levelWindow = null;
            _levelWindow.Show();
        }
        else
        {
            _levelWindow.Activate();
        }
    }

    private void OnShowClassification(object? sender, RoutedEventArgs e)
    {
        if (_classificationWindow is null)
        {
            _classificationWindow = new ClassificationWindow(_viewModel);
            _classificationWindow.Closed += (_, _) => _classificationWindow = null;
            _classificationWindow.Show();
        }
        else
        {
            _classificationWindow.Activate();
        }
    }

    private void OnClassificationReady(ClassificationResult[] results)
        => Dispatcher.UIThread.Post(() => _viewModel.UpdateClassification(results));

    private void OnResultsReady(IReadOnlyList<PairResult> results)
    {
        Dispatcher.UIThread.Post(() => _viewModel.UpdateReadouts(results));
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Engine.ResultsReady -= OnResultsReady;
        _viewModel.Engine.ChannelLevelsReady -= OnChannelLevels;
        _viewModel.Engine.AzimuthReady -= OnAzimuth;
        _viewModel.Engine.ClassificationReady -= OnClassificationReady;
        _viewModel.ReplayFinished -= OnReplayFinished;
        _viewModel.Classifier.Dispose();
        _viewModel.Engine.Stop();
        _viewModel.Shutdown();

        // No longer owned windows (so they don't minimize with this window) — close them explicitly.
        _arrayMap?.Close();
        _beamWindow?.Close();
        _delayWindow?.Close();
        _classificationWindow?.Close();

        base.OnClosed(e);
    }
}
