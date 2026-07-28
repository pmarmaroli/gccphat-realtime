using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using GccPhat.RealTime.ViewModels;
using ScottPlot.Plottables;

namespace GccPhat.RealTime;

public partial class LevelViewWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Dictionary<int, DataLogger> _loggers = new();
    private readonly Dictionary<int, Queue<(double t, double d)>> _recent = new();
    private double _lastTimeSeconds;
    private double _dataYMin = double.PositiveInfinity;
    private double _dataYMax = double.NegativeInfinity;

    public LevelViewWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        StylePlot();
        BuildLoggers();

        _viewModel.ChannelMeters.CollectionChanged += OnChannelMetersChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Engine.ChannelLevelsReady += OnChannelLevelsReady;
    }

    private void StylePlot()
    {
        ScottPlot.Color figure = ScottPlot.Color.FromHex("#0B0F14");
        ScottPlot.Color panel = ScottPlot.Color.FromHex("#121A24");
        ScottPlot.Color grid = ScottPlot.Color.FromHex("#1E2A38");
        ScottPlot.Color text = ScottPlot.Color.FromHex("#9FB3C8");
        ScottPlot.Color accent = ScottPlot.Color.FromHex("#19D3FF");

        LevelPlot.Plot.FigureBackground.Color = figure;
        LevelPlot.Plot.DataBackground.Color = panel;
        LevelPlot.Plot.Axes.Color(text);
        LevelPlot.Plot.Grid.MajorLineColor = grid;

        LevelPlot.Plot.Legend.BackgroundColor = panel;
        LevelPlot.Plot.Legend.FontColor = text;
        LevelPlot.Plot.Legend.OutlineColor = grid;

        LevelPlot.Plot.Title("Per-channel signal level");
        LevelPlot.Plot.Axes.Title.Label.ForeColor = accent;
        LevelPlot.Plot.Axes.Title.Label.FontSize = 20;
        LevelPlot.Plot.XLabel("Time (s)");
        LevelPlot.Plot.YLabel("Level (dBFS)");
        LevelPlot.Plot.Axes.Left.Label.FontSize = 16;
        LevelPlot.Plot.Axes.Bottom.Label.FontSize = 16;
        LevelPlot.Plot.Axes.Left.TickLabelStyle.FontSize = 14;
        LevelPlot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 14;
        LevelPlot.Plot.Legend.FontSize = 15;
        LevelPlot.Plot.ShowLegend();

        LevelPlot.Refresh();
    }

    private void OnChannelMetersChanged(object? sender, NotifyCollectionChangedEventArgs e) => BuildLoggers();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsRunning) when _viewModel.IsRunning:
                BuildLoggers();
                break;
            case nameof(MainViewModel.LevelXAutoScale):
            case nameof(MainViewModel.LevelXWindowSeconds):
            case nameof(MainViewModel.LevelYAutoScale):
            case nameof(MainViewModel.LevelYMin):
            case nameof(MainViewModel.LevelYMax):
                ApplyAxes();
                break;
        }
    }

    private void BuildLoggers()
    {
        _loggers.Clear();
        _recent.Clear();
        LevelPlot.Plot.Clear();
        _lastTimeSeconds = 0;
        _dataYMin = double.PositiveInfinity;
        _dataYMax = double.NegativeInfinity;

        StylePlot();

        foreach (ChannelMeterViewModel vm in _viewModel.ChannelMeters)
        {
            DataLogger logger = LevelPlot.Plot.Add.DataLogger();
            (byte r, byte g, byte b) = Palette.Get(vm.Index);
            logger.Color = new ScottPlot.Color(r, g, b);
            logger.LineStyle.Width = 2.5f;
            logger.LegendText = vm.Name;
            logger.ManageAxisLimits = false;
            _loggers[vm.Index] = logger;
            _recent[vm.Index] = new Queue<(double, double)>();
        }

        ApplyAxes();
        UpdateSummary();
    }

    private void OnChannelLevelsReady(double[] levels)
    {
        Dispatcher.BeginInvoke(() =>
        {
            double t = _viewModel.Engine.ElapsedSeconds;
            for (int c = 0; c < levels.Length; c++)
            {
                if (!_loggers.TryGetValue(c, out DataLogger? logger))
                {
                    continue;
                }

                double db = levels[c] <= 1e-7 ? -160.0 : 20.0 * Math.Log10(levels[c]);
                logger.Add(t, db);
                if (_recent.TryGetValue(c, out Queue<(double, double)>? q))
                {
                    q.Enqueue((t, db));
                }
            }

            _lastTimeSeconds = t;
            ApplyAxes();
            UpdateSummary();
        });
    }

    private void ApplyAxes()
    {
        double tMax = _lastTimeSeconds;
        double window = Math.Max(1, _viewModel.LevelXWindowSeconds);
        double xMin;
        double xMax;

        if (_viewModel.LevelXAutoScale)
        {
            xMax = Math.Max(window, tMax);
            xMin = xMax - window;
        }
        else
        {
            xMax = Math.Max(window, tMax);
            xMin = Math.Max(0, xMax - window);
        }

        LevelPlot.Plot.Axes.SetLimitsX(xMin, xMax);

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
        if (_viewModel.LevelYAutoScale)
        {
            if (_dataYMax >= _dataYMin)
            {
                double pad = Math.Max(0.5, (_dataYMax - _dataYMin) * 0.1);
                yMin = _dataYMin - pad;
                yMax = _dataYMax + pad;
            }
            else
            {
                yMin = -60;
                yMax = 0;
            }
        }
        else
        {
            yMin = _viewModel.LevelYMin;
            yMax = _viewModel.LevelYMax;
        }

        LevelPlot.Plot.Axes.SetLimitsY(yMin, yMax);
        LevelPlot.Refresh();
    }

    private void UpdateSummary()
    {
        SummaryText.Text = _viewModel.ChannelMeters.Count == 0
            ? "Select a capture device or WAV file to start the level workspace."
            : $"Channels: {_viewModel.ChannelMeters.Count}   Window: {_viewModel.LevelXWindowSeconds:F0}s   Y: {(_viewModel.LevelYAutoScale ? "auto" : $"{_viewModel.LevelYMin:F1}..{_viewModel.LevelYMax:F1} dB")}";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.ChannelMeters.CollectionChanged -= OnChannelMetersChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Engine.ChannelLevelsReady -= OnChannelLevelsReady;
        base.OnClosed(e);
    }
}
