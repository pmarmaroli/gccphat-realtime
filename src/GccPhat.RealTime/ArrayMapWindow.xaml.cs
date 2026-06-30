using System;
using System.Collections.Specialized;
using System.ComponentModel;
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
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.MicPositions.CollectionChanged += OnPositionsChanged;
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
        AzimuthLabel.Text = $"Azimuth: {_viewModel.AzimuthDeg:F1}\u00b0   {_viewModel.LevelText}";

        Brush dim = (Brush)FindResource("TextDimBrush");
        Brush text = (Brush)FindResource("TextBrush");
        Brush accent = (Brush)FindResource("AccentBrush");

        ArrayGeometryCanvasDrawing.DrawCompass(c, w, h, dim);

        // Gate: only show the array + arrow when the loudest channel is above the threshold.
        if (!_viewModel.IsAboveThreshold)
        {
            ArrayGeometryCanvasDrawing.AddText(c, "below threshold", cx - 50, cy - 8, dim);
            return;
        }

        // Mic positions, scaled to fit the ring.
        double scale = ArrayGeometryCanvasDrawing.ComputeGeometryScale(_viewModel.MicPositions, radius);
        foreach (MicGeometryViewModel pos in _viewModel.MicPositions)
        {
            double px = cx + pos.X * scale;
            double py = cy - pos.Y * scale; // screen y is down
            ArrayGeometryCanvasDrawing.DrawMicrophone(c, px, py, accent, accent, text, pos.Channel is int ch ? ch.ToString() : "-");
        }

        // Azimuth arrow (0deg = +X, CCW).
        ArrayGeometryCanvasDrawing.DrawAzimuthArrow(c, cx, cy, radius, _viewModel.AzimuthDeg, accent);
        ArrayGeometryCanvasDrawing.DrawCenter(c, cx, cy, text);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.MicPositions.CollectionChanged -= OnPositionsChanged;
        base.OnClosed(e);
    }
}
