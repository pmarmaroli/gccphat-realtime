using System;
using System.Collections.Generic;
using System.Windows;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.ViewModels;
using Microsoft.Win32;

namespace GccPhat.RealTime;

/// <summary>Interaction logic for MainWindow.xaml.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.Engine.ResultsReady += OnResultsReady;
        _viewModel.Engine.ChannelLevelsReady += OnChannelLevels;
        _viewModel.Engine.AzimuthReady += OnAzimuth;
        _viewModel.Engine.ClassificationReady += OnClassificationReady;
        _viewModel.ReplayFinished += OnReplayFinished;
    }

    private void OnBrowseReplayFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "WAV audio (*.wav)|*.wav", Title = "Select multichannel WAV file" };
        if (dlg.ShowDialog() == true)
        {
            _viewModel.LoadReplayFile(dlg.FileName);
        }
    }

    // Raised on the replay pump thread when a file finishes playing on its own.
    private void OnReplayFinished(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() => _viewModel.StopCommand.Execute(null));

    private void OnChannelLevels(double[] levels)
        => Dispatcher.BeginInvoke(() => _viewModel.UpdateChannelLevels(levels));

    private void OnAzimuth(GccPhat.Core.SrpEstimate estimate)
        => Dispatcher.BeginInvoke(() => _viewModel.UpdateAzimuth(estimate));

    private ArrayMapWindow? _arrayMap;
    private BeamformerWindow? _beamWindow;
    private DelayViewWindow? _delayWindow;
    private ClassificationWindow? _classificationWindow;

    // Shared across all open MainWindow instances (not owned by any single analysis session).
    private static CombinedLocalizationWindow? s_combinedWindow;

    private void OnShowCombinedLocalization(object sender, RoutedEventArgs e)
    {
        if (s_combinedWindow is null)
        {
            s_combinedWindow = new CombinedLocalizationWindow();
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
    private void OnNewWindow(object sender, RoutedEventArgs e) => new MainWindow().Show();

    private void OnShowArrayMap(object sender, RoutedEventArgs e)
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

    private void OnShowBeamformer(object sender, RoutedEventArgs e)
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

    private void OnShowDelayView(object sender, RoutedEventArgs e)
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

    private void OnShowClassification(object sender, RoutedEventArgs e)
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
        => Dispatcher.BeginInvoke(() => _viewModel.UpdateClassification(results));

    private void OnResultsReady(IReadOnlyList<PairResult> results)
    {
        Dispatcher.BeginInvoke(() => _viewModel.UpdateReadouts(results));
    }

    protected override void OnClosed(System.EventArgs e)
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
