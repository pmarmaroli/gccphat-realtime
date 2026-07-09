using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GccPhat.RealTime.ViewModels;

namespace GccPhat.RealTime;

/// <summary>
/// Compass-style array map: draws the microphone positions with their channel numbers and an
/// arrow pointing at the latest SRP-PHAT source azimuth. Redraws live as the azimuth updates.
/// </summary>
public partial class ArrayMapWindow : Window
{
    private readonly MainViewModel _viewModel;

    public ArrayMapWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.TryAutoApplyDefaultLocalizationPairs();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.MicPositions.CollectionChanged += OnPositionsChanged;
        _viewModel.ActivePairs.CollectionChanged += OnPositionsChanged;
        Loaded += (_, _) => Redraw();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void OnPositionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Redraw();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.AzimuthDeg):
            case nameof(MainViewModel.AzimuthText):
            case nameof(MainViewModel.CurrentLevelDb):
            case nameof(MainViewModel.LevelThresholdDb):
            case nameof(MainViewModel.CanLocalizeWithCurrentPairs):
            case nameof(MainViewModel.HasVisibleLocalizationAzimuth):
            case nameof(MainViewModel.ShowHemisphere):
            case nameof(MainViewModel.HasFrontBackAmbiguity):
                Redraw();
                break;
        }
    }

    private void Redraw()
    {
        Canvas c = MapCanvas;
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 20 || h < 20) return;

        double cx = w / 2, cy = h / 2;
        double radius = Math.Min(w, h) / 2 - 50;
        string azimuthText = _viewModel.HasVisibleLocalizationAzimuth
            ? $"{_viewModel.AzimuthDeg:F1}\u00b0"
            : "--";
        AzimuthLabel.Text = $"Azimuth: {azimuthText}   {_viewModel.LevelText}";

        Brush dim = (Brush)FindResource("TextDimBrush");
        Brush text = (Brush)FindResource("TextBrush");
        Brush accent = (Brush)FindResource("AccentBrush");

        ArrayGeometryCanvasDrawing.DrawCompass(c, w, h, dim);

        if (_viewModel.HasFrontBackAmbiguity)
        {
            ArrayGeometryCanvasDrawing.DrawAmbiguityMask(c, cx, cy, radius, dim);
        }

        // Mic positions and pair lines are always visible regardless of threshold.
        double scale = ArrayGeometryCanvasDrawing.ComputeGeometryScale(_viewModel.MicPositions, radius);

        // Build channel→canvas position lookup for drawing pair lines.
        Dictionary<int, (double X, double Y)> channelScreen = _viewModel.MicPositions
            .Where(p => p.Channel is int)
            .ToDictionary(p => p.Channel!.Value, p => (cx + p.X * scale, cy - p.Y * scale));

        // Draw pair lines behind the mic dots.
        foreach (PairViewModel pair in _viewModel.ActivePairs)
        {
            if (channelScreen.TryGetValue(pair.Pair.ChannelA, out var posA) &&
                channelScreen.TryGetValue(pair.Pair.ChannelB, out var posB))
            {
                bool isUsed = pair.LocalizationState == LocalizationPairState.Used;
                ArrayGeometryCanvasDrawing.DrawPairLine(c, posA.X, posA.Y, posB.X, posB.Y, pair.ColorBrush, isUsed);
            }
        }

        // Mic position dots on top.
        foreach (MicGeometryViewModel pos in _viewModel.MicPositions)
        {
            double px = cx + pos.X * scale;
            double py = cy - pos.Y * scale;
            ArrayGeometryCanvasDrawing.DrawMicrophone(c, px, py, accent, accent, text, pos.Channel is int ch ? ch.ToString() : "-");
        }

        // Gate: status text and azimuth arrow only appear above threshold.
        if (!_viewModel.IsAboveThreshold)
        {
            ArrayGeometryCanvasDrawing.AddText(c, "below threshold", cx - 50, cy - 8, dim);
            return;
        }

        if (!_viewModel.CanLocalizeWithCurrentPairs)
        {
            ArrayGeometryCanvasDrawing.AddText(c, "localization waiting", cx - 74, cy - 8, dim);
            ArrayGeometryCanvasDrawing.DrawCenter(c, cx, cy, text);
            return;
        }

        if (!_viewModel.HasVisibleLocalizationAzimuth)
        {
            ArrayGeometryCanvasDrawing.AddText(c, "waiting for estimate", cx - 72, cy - 8, dim);
            ArrayGeometryCanvasDrawing.DrawCenter(c, cx, cy, text);
            return;
        }

        Color accentColor = accent is SolidColorBrush scb ? scb.Color : Colors.Cyan;

        // Front/back-ambiguous arrays only scan [180°, 360°) — see SrpPhatLocalizer._searchBinStart.
        double azStart = _viewModel.HasFrontBackAmbiguity ? 180.0 : 0.0;

        // Power visualization: hemisphere heat map (if enabled) or 2D polar spectrum.
        if (_viewModel.ShowHemisphere && _viewModel.HemispherePowers is double[,] hemi)
        {
            ArrayGeometryCanvasDrawing.DrawHemisphereHeatMap(c, cx, cy, radius, hemi,
                _viewModel.HemiElStepDeg, _viewModel.SrpSpectrumStepDeg, accentColor, azStart);
        }
        else if (_viewModel.SrpSpectrum is double[] spectrum && spectrum.Length > 0)
        {
            ArrayGeometryCanvasDrawing.DrawSrpSpectrum(c, cx, cy, radius, spectrum, _viewModel.SrpSpectrumStepDeg, accentColor, azStart);
        }

        // Azimuth arrow (0deg = +X, CCW).
        ArrayGeometryCanvasDrawing.DrawAzimuthArrow(c, cx, cy, radius, _viewModel.AzimuthDeg, accent);
        ArrayGeometryCanvasDrawing.DrawCenter(c, cx, cy, text);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.MicPositions.CollectionChanged -= OnPositionsChanged;
        _viewModel.ActivePairs.CollectionChanged -= OnPositionsChanged;
        base.OnClosed(e);
    }
}
