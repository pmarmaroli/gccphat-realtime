using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.ViewModels;
using ScottPlot.Plottables;

namespace GccPhat.RealTime;

public partial class DelayViewWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Dictionary<ChannelPair, DataLogger> _loggers = new();
    private readonly Dictionary<ChannelPair, Queue<(double t, double d)>> _recent = new();
    private double _lastTimeSeconds;
    private double _dataYMin = double.PositiveInfinity;
    private double _dataYMax = double.NegativeInfinity;

    public DelayViewWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        StylePlot();
        BuildLoggers();

        _viewModel.ActivePairs.CollectionChanged += OnActivePairsChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Engine.ResultsReady += OnResultsReady;
    }

    private void StylePlot()
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
        DelayPlot.Plot.Axes.Title.Label.FontSize = 20;
        DelayPlot.Plot.XLabel("Time (s)");
        DelayPlot.Plot.YLabel("Delay (ms)");
        DelayPlot.Plot.Axes.Left.Label.FontSize = 16;
        DelayPlot.Plot.Axes.Bottom.Label.FontSize = 16;
        DelayPlot.Plot.Axes.Left.TickLabelStyle.FontSize = 14;
        DelayPlot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 14;
        DelayPlot.Plot.Legend.FontSize = 15;
        DelayPlot.Plot.ShowLegend();

        DelayPlot.Refresh();
    }

    private void OnActivePairsChanged(object? sender, NotifyCollectionChangedEventArgs e) => BuildLoggers();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsRunning) when _viewModel.IsRunning:
                BuildLoggers();
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
        _recent.Clear();
        DelayPlot.Plot.Clear();
        _lastTimeSeconds = 0;
        _dataYMin = double.PositiveInfinity;
        _dataYMax = double.NegativeInfinity;

        StylePlot();

        foreach (PairViewModel vm in _viewModel.ActivePairs)
        {
            DataLogger logger = DelayPlot.Plot.Add.DataLogger();
            (byte r, byte g, byte b) = Palette.Get(vm.PaletteIndex);
            logger.Color = new ScottPlot.Color(r, g, b);
            logger.LineStyle.Width = 2.5f;
            logger.LegendText = vm.Label;
            logger.ManageAxisLimits = false;
            _loggers[vm.Pair] = logger;
            _recent[vm.Pair] = new Queue<(double, double)>();
        }

        ApplyAxes();
        UpdateSummary();
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
                    if (_recent.TryGetValue(result.Pair, out Queue<(double, double)>? q))
                    {
                        q.Enqueue((result.TimeSeconds, result.DelayMs));
                    }
                }
            }

            if (results.Count > 0)
            {
                _lastTimeSeconds = results[0].TimeSeconds;
            }

            ApplyAxes();
            UpdateSummary();
        });
    }

    private void ApplyAxes()
    {
        double tMax = _lastTimeSeconds;
        double window = Math.Max(1, _viewModel.XWindowSeconds);
        double xMin;
        double xMax;

        if (_viewModel.XAutoScale)
        {
            xMax = Math.Max(window, tMax);
            xMin = xMax - window;
        }
        else
        {
            xMax = Math.Max(window, tMax);
            xMin = Math.Max(0, xMax - window);
        }

        DelayPlot.Plot.Axes.SetLimitsX(xMin, xMax);

        double yLo = double.PositiveInfinity;
        double yHi = double.NegativeInfinity;
        foreach (Queue<(double t, double d)> q in _recent.Values)
        {
            while (q.Count > 0 && q.Peek().t < xMin)
            {
                q.Dequeue();
            }

            foreach ((double _, double d) in q)
            {
                if (d < yLo) yLo = d;
                if (d > yHi) yHi = d;
            }
        }

        _dataYMin = yLo;
        _dataYMax = yHi;

        double yMin;
        double yMax;
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

    private void UpdateSummary()
    {
        SummaryText.Text = _viewModel.ActivePairs.Count == 0
            ? "Add one or more channel pairs to start the delay workspace."
            : $"Pairs: {_viewModel.ActivePairs.Count}   Window: {_viewModel.XWindowSeconds:F0}s   Y: {(_viewModel.YAutoScale ? "auto" : $"{_viewModel.YMin:F1}..{_viewModel.YMax:F1} ms")}";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.ActivePairs.CollectionChanged -= OnActivePairsChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Engine.ResultsReady -= OnResultsReady;
        base.OnClosed(e);
    }
}
