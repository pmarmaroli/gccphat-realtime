using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.ViewModels;
using ScottPlot.Plottables;

namespace GccPhat.RealTime;

/// <summary>Interaction logic for MainWindow.xaml.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly Dictionary<ChannelPair, DataLogger> _loggers = new();
    private double _lastTimeSeconds;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        DelayPlot.Plot.Title("Inter-microphone delay (GCC-PHAT)");
        DelayPlot.Plot.XLabel("Time (s)");
        DelayPlot.Plot.YLabel("Delay (ms)");
        DelayPlot.Plot.ShowLegend();

        _viewModel.ActivePairs.CollectionChanged += OnActivePairsChanged;
        _viewModel.Engine.ResultsReady += OnResultsReady;
        _viewModel.Engine.ChannelLevelsReady += OnChannelLevels;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        BuildLoggers();
    }

    private void OnChannelLevels(double[] levels)
        => Dispatcher.BeginInvoke(() => _viewModel.UpdateChannelLevels(levels));

    private void OnActivePairsChanged(object? sender, NotifyCollectionChangedEventArgs e) => BuildLoggers();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.YAutoScale)
            or nameof(MainViewModel.YMin)
            or nameof(MainViewModel.YMax))
        {
            ApplyYAxis();
        }
    }

    private void BuildLoggers()
    {
        _loggers.Clear();
        DelayPlot.Plot.Clear();

        foreach (PairViewModel vm in _viewModel.ActivePairs)
        {
            DataLogger logger = DelayPlot.Plot.Add.DataLogger();
            (byte r, byte g, byte b) = Palette.Get(vm.PaletteIndex);
            logger.Color = new ScottPlot.Color(r, g, b);
            logger.LegendText = vm.Label;
            logger.ManageAxisLimits = _viewModel.YAutoScale;
            _loggers[vm.Pair] = logger;
        }

        ApplyYAxis();
    }

    /// <summary>Applies the current Y-axis mode (auto-scale, or fixed user min/max).</summary>
    private void ApplyYAxis()
    {
        foreach (DataLogger logger in _loggers.Values)
        {
            logger.ManageAxisLimits = _viewModel.YAutoScale;
        }

        if (!_viewModel.YAutoScale)
        {
            DelayPlot.Plot.Axes.SetLimitsX(0, Math.Max(5, _lastTimeSeconds));
            DelayPlot.Plot.Axes.SetLimitsY(_viewModel.YMin, _viewModel.YMax);
        }

        DelayPlot.Refresh();
    }

    private void OnResultsReady(IReadOnlyList<PairResult> results)
    {
        Dispatcher.BeginInvoke(() =>
        {
            foreach (PairResult result in results)
            {
                if (result.Valid && _loggers.TryGetValue(result.Pair, out DataLogger? logger))
                {
                    logger.Add(result.TimeSeconds, result.DelayMs);
                }
            }

            if (results.Count > 0)
            {
                _lastTimeSeconds = results[0].TimeSeconds;
            }

            _viewModel.UpdateReadouts(results);

            if (!_viewModel.YAutoScale)
            {
                DelayPlot.Plot.Axes.SetLimitsX(0, Math.Max(5, _lastTimeSeconds));
                DelayPlot.Plot.Axes.SetLimitsY(_viewModel.YMin, _viewModel.YMax);
            }

            DelayPlot.Refresh();
        });
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _viewModel.Engine.ResultsReady -= OnResultsReady;
        _viewModel.Engine.ChannelLevelsReady -= OnChannelLevels;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Engine.Stop();
        base.OnClosed(e);
    }
}
