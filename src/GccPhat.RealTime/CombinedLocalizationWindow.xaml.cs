using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GccPhat.RealTime.ViewModels;

namespace GccPhat.RealTime;

/// <summary>
/// Combines the live SRP-PHAT azimuth from two open analysis windows into a single 2D (x,y)
/// source fix: draws both array origins and their extended bearing rays on a shared canvas, with
/// the computed intersection marked. Not owned by any single analysis window — see the static
/// singleton in <see cref="MainWindow"/>.
/// </summary>
public partial class CombinedLocalizationWindow : Window
{
    private readonly CombinedLocalizationViewModel _viewModel = new();
    private bool _redrawPending;

    public CombinedLocalizationWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => Redraw();
    }

    private void OnNewAnalysisWindow(object sender, RoutedEventArgs e) => new MainWindow().Show();

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    // Both live sessions push azimuth updates at the analysis tick rate (20 Hz by default each),
    // and every Recompute() touches three properties (SourceXCm/SourceYCm/FixStatusText) — without
    // coalescing this fires dozens of full canvas rebuilds per second. Collapse any burst of
    // changes into a single redraw per render frame.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_redrawPending)
        {
            return;
        }
        _redrawPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _redrawPending = false;
            Redraw();
        }));
    }

    private void Redraw()
    {
        Canvas c = MapCanvas;
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 20 || h < 20)
        {
            return;
        }

        Brush dim = (Brush)FindResource("TextDimBrush");
        Brush text = (Brush)FindResource("TextBrush");
        Brush colorA = (Brush)FindResource("AccentBrush");
        Brush colorB = (Brush)FindResource("WarnBrush");
        Brush fixBrush = (Brush)FindResource("GoodBrush");

        MainViewModel? sessionA = _viewModel.SessionA;
        MainViewModel? sessionB = _viewModel.SessionB;
        if (sessionA is null || sessionB is null)
        {
            ArrayGeometryCanvasDrawing.AddText(c, "Pick two analysis windows to combine.", w / 2 - 100, h / 2 - 8, dim);
            return;
        }

        // Array origins in metres (global frame: A at the origin, B offset by the entered cm values).
        double bxM = _viewModel.OffsetXCm / 100.0;
        double byM = _viewModel.OffsetYCm / 100.0;
        double? fixXM = _viewModel.SourceXCm is double sx ? sx / 100.0 : null;
        double? fixYM = _viewModel.SourceYCm is double sy ? sy / 100.0 : null;

        // Bounding box in metres: both origins, plus the fix point (if any), plus a margin.
        double minX = Math.Min(0, bxM), maxX = Math.Max(0, bxM);
        double minY = Math.Min(0, byM), maxY = Math.Max(0, byM);
        if (fixXM is double fx && fixYM is double fy)
        {
            minX = Math.Min(minX, fx); maxX = Math.Max(maxX, fx);
            minY = Math.Min(minY, fy); maxY = Math.Max(maxY, fy);
        }

        double spanM = Math.Max(Math.Max(maxX - minX, maxY - minY), 0.5); // floor avoids a degenerate zero span
        double halfSpanM = spanM / 2.0 * 1.4; // 40% margin so rays/markers aren't flush against the edge
        double centerXM = (minX + maxX) / 2.0;
        double centerYM = (minY + maxY) / 2.0;

        double usableRadius = Math.Min(w, h) / 2.0 - 40.0;
        if (usableRadius < 10)
        {
            return;
        }
        double scale = usableRadius / halfSpanM;
        double cx = w / 2.0, cy = h / 2.0;

        (double X, double Y) ToPixel(double xM, double yM) => (cx + (xM - centerXM) * scale, cy - (yM - centerYM) * scale);

        (double X, double Y) originA = ToPixel(0, 0);
        (double X, double Y) originB = ToPixel(bxM, byM);
        double rayLength = usableRadius * 1.3; // px — long enough to visually cross the canvas

        if (sessionA.HasVisibleLocalizationAzimuth)
        {
            ArrayGeometryCanvasDrawing.DrawAzimuthArrow(c, originA.X, originA.Y, rayLength, sessionA.AzimuthDeg, colorA);
        }
        else
        {
            ArrayGeometryCanvasDrawing.AddText(c, "no live azimuth", originA.X + 8, originA.Y + 10, dim);
        }

        if (sessionB.HasVisibleLocalizationAzimuth)
        {
            ArrayGeometryCanvasDrawing.DrawAzimuthArrow(c, originB.X, originB.Y, rayLength, sessionB.AzimuthDeg, colorB);
        }
        else
        {
            ArrayGeometryCanvasDrawing.AddText(c, "no live azimuth", originB.X + 8, originB.Y + 10, dim);
        }

        ArrayGeometryCanvasDrawing.DrawCenter(c, originA.X, originA.Y, colorA);
        ArrayGeometryCanvasDrawing.AddText(c, "A: " + sessionA.SessionLabel, originA.X + 8, originA.Y - 20, text);
        ArrayGeometryCanvasDrawing.DrawCenter(c, originB.X, originB.Y, colorB);
        ArrayGeometryCanvasDrawing.AddText(c, "B: " + sessionB.SessionLabel, originB.X + 8, originB.Y - 20, text);

        if (fixXM is double fixX && fixYM is double fixY)
        {
            (double X, double Y) fixPx = ToPixel(fixX, fixY);
            ArrayGeometryCanvasDrawing.DrawSourceMarker(c, fixPx.X, fixPx.Y, fixBrush);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
