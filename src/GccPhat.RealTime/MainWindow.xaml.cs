using System.Collections.Generic;
using System.Collections.Specialized;
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

        BuildLoggers();
    }

    private void OnActivePairsChanged(object? sender, NotifyCollectionChangedEventArgs e) => BuildLoggers();

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
            _loggers[vm.Pair] = logger;
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

            _viewModel.UpdateReadouts(results);
            DelayPlot.Refresh();
        });
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _viewModel.Engine.ResultsReady -= OnResultsReady;
        _viewModel.Engine.Stop();
        base.OnClosed(e);
    }
}
