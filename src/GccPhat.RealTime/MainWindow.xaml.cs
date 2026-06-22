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
    private double _dataYMin = double.PositiveInfinity;
    private double _dataYMax = double.NegativeInfinity;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        StyleDarkPlot();

        _viewModel.ActivePairs.CollectionChanged += OnActivePairsChanged;
        _viewModel.Engine.ResultsReady += OnResultsReady;
        _viewModel.Engine.ChannelLevelsReady += OnChannelLevels;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        BuildLoggers();
    }

    private void OnChannelLevels(double[] levels)
        => Dispatcher.BeginInvoke(() => _viewModel.UpdateChannelLevels(levels));

    private void StyleDarkPlot()
    {
        ScottPlot.Color figure = ScottPlot.Color.FromHex("#0B0F14");
        ScottPlot.Color panel = ScottPlot.Color.FromHex("#121A24");
        ScottPlot.Color grid = ScottPlot.Color.FromHex("#1E2A38");
        ScottPlot.Color text = ScottPlot.Color.FromHex("#9FB3C8");
        ScottPlot.Color accent = ScottPlot.Color.FromHex("#19D3FF");

        DelayPlot.Plot.FigureBackground.Color = figure;
        DelayPlot.Plot.DataBackground.Color = panel;
        DelayPlot.Plot.Axes.Color(text);
        DelayPlot.Plot.Grid.MajorLineColor = grid;

        DelayPlot.Plot.Legend.BackgroundColor = panel;
        DelayPlot.Plot.Legend.FontColor = text;
        DelayPlot.Plot.Legend.OutlineColor = grid;

        DelayPlot.Plot.Title("Inter-microphone delay (GCC-PHAT)");
        DelayPlot.Plot.Axes.Title.Label.ForeColor = accent;
        DelayPlot.Plot.XLabel("Time (s)");
        DelayPlot.Plot.YLabel("Delay (ms)");
        DelayPlot.Plot.ShowLegend();

        DelayPlot.Refresh();
    }

    private void OnActivePairsChanged(object? sender, NotifyCollectionChangedEventArgs e) => BuildLoggers();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsRunning) when _viewModel.IsRunning:
                BuildLoggers(); // start fresh: clear history (the engine clock restarts at 0)
                break;
            case nameof(MainViewModel.XAutoScale):
            case nameof(MainViewModel.XWindowSeconds):
            case nameof(MainViewModel.YAutoScale):
            case nameof(MainViewModel.YMin):
            case nameof(MainViewModel.YMax):
                ApplyAxes();
                break;
        }
    }

    private void BuildLoggers()
    {
        _loggers.Clear();
        DelayPlot.Plot.Clear();
        _lastTimeSeconds = 0;
        _dataYMin = double.PositiveInfinity;
        _dataYMax = double.NegativeInfinity;

        foreach (PairViewModel vm in _viewModel.ActivePairs)
        {
            DataLogger logger = DelayPlot.Plot.Add.DataLogger();
            (byte r, byte g, byte b) = Palette.Get(vm.PaletteIndex);
            logger.Color = new ScottPlot.Color(r, g, b);
            logger.LegendText = vm.Label;
            logger.ManageAxisLimits = false; // we drive both axes from the X/Y settings
            _loggers[vm.Pair] = logger;
        }

        ApplyAxes();
    }

    /// <summary>Applies the current X (time window) and Y (delay range) axis settings.</summary>
    private void ApplyAxes()
    {
        double tMax = _lastTimeSeconds;
        double xMin, xMax;
        if (_viewModel.XAutoScale)
        {
            xMin = 0;
            xMax = Math.Max(5, tMax);
        }
        else
        {
            double window = Math.Max(1, _viewModel.XWindowSeconds);
            xMax = Math.Max(window, tMax);
            xMin = Math.Max(0, xMax - window);
        }
        DelayPlot.Plot.Axes.SetLimitsX(xMin, xMax);

        double yMin, yMax;
        if (_viewModel.YAutoScale)
        {
            if (_dataYMax >= _dataYMin)
            {
                double pad = Math.Max(0.5, (_dataYMax - _dataYMin) * 0.1);
                yMin = _dataYMin - pad;
                yMax = _dataYMax + pad;
            }
            else
            {
                yMin = -1;
                yMax = 1;
            }
        }
        else
        {
            yMin = _viewModel.YMin;
            yMax = _viewModel.YMax;
        }
        DelayPlot.Plot.Axes.SetLimitsY(yMin, yMax);

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
                    if (result.DelayMs < _dataYMin) _dataYMin = result.DelayMs;
                    if (result.DelayMs > _dataYMax) _dataYMax = result.DelayMs;
                }
            }

            if (results.Count > 0)
            {
                _lastTimeSeconds = results[0].TimeSeconds;
            }

            _viewModel.UpdateReadouts(results);
            ApplyAxes();
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
