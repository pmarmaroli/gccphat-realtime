using System;
using System.Collections.Generic;
using System.Windows;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.ViewModels;

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
    }

    private void OnChannelLevels(double[] levels)
        => Dispatcher.BeginInvoke(() => _viewModel.UpdateChannelLevels(levels));

    private void OnAzimuth(GccPhat.Core.SrpEstimate estimate)
        => Dispatcher.BeginInvoke(() => _viewModel.UpdateAzimuth(estimate));

    private ArrayMapWindow? _arrayMap;
    private BeamformerWindow? _beamWindow;
    private DelayViewWindow? _delayWindow;

    private void OnShowArrayMap(object sender, RoutedEventArgs e)
    {
        if (_arrayMap is null)
        {
            _arrayMap = new ArrayMapWindow(_viewModel) { Owner = this };
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
            _beamWindow = new BeamformerWindow(_viewModel) { Owner = this };
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
            _delayWindow = new DelayViewWindow(_viewModel) { Owner = this };
            _delayWindow.Closed += (_, _) => _delayWindow = null;
            _delayWindow.Show();
        }
        else
        {
            _delayWindow.Activate();
        }
    }

    private void OnResultsReady(IReadOnlyList<PairResult> results)
    {
        Dispatcher.BeginInvoke(() => _viewModel.UpdateReadouts(results));
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _viewModel.Engine.ResultsReady -= OnResultsReady;
        _viewModel.Engine.ChannelLevelsReady -= OnChannelLevels;
        _viewModel.Engine.AzimuthReady -= OnAzimuth;
        _viewModel.Engine.Stop();
        base.OnClosed(e);
    }
}
