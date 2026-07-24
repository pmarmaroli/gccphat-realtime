using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GccPhat.RealTime.ViewModels;

namespace GccPhat.RealTime;

internal static class ArrayGeometryCanvasDrawing
{
    public static (double CenterX, double CenterY, double Radius) DrawCompass(Canvas canvas, double width, double height, IBrush dim)
    {
        double centerX = width / 2.0;
        double centerY = height / 2.0;
        double radius = Math.Min(width, height) / 2.0 - 50.0;

        var ring = new Ellipse
        {
            Width = radius * 2.0,
            Height = radius * 2.0,
            Stroke = dim,
            StrokeThickness = 1.0
        };
        Canvas.SetLeft(ring, centerX - radius);
        Canvas.SetTop(ring, centerY - radius);
        canvas.Children.Add(ring);

        AddText(canvas, "0°", centerX + radius + 6.0, centerY - 10.0, dim);
        AddText(canvas, "90°", centerX - 14.0, centerY - radius - 22.0, dim);
        AddText(canvas, "180°", centerX - radius - 40.0, centerY - 10.0, dim);
        AddText(canvas, "270°", centerX - 22.0, centerY + radius + 4.0, dim);
        return (centerX, centerY, radius);
    }

    public static double ComputeGeometryScale(IEnumerable<MicGeometryViewModel> positions, double radius)
    {
        double maxRadius = positions
            .Select(pos => Math.Sqrt(pos.X * pos.X + pos.Y * pos.Y))
            .DefaultIfEmpty(0.0)
            .Max();

        return maxRadius > 1e-6 ? radius * 0.7 / maxRadius : 1.0;
    }

    public static void DrawMicrophone(Canvas canvas, double x, double y, IBrush fill, IBrush stroke, IBrush labelBrush, string label)
    {
        var marker = new Ellipse
        {
            Width = 14.0,
            Height = 14.0,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = 1.0
        };
        Canvas.SetLeft(marker, x - 7.0);
        Canvas.SetTop(marker, y - 7.0);
        canvas.Children.Add(marker);
        AddText(canvas, label, x + 8.0, y - 22.0, labelBrush);
    }

    /// <summary>
    /// Renders the SRP-PHAT hemisphere power map as a heat-map image using an azimuthal equidistant
    /// projection: center = zenith (el=90°), outer ring = horizon (el=0°).
    /// For a planar array the map will show arcs (not points), correctly exposing the elevation ambiguity.
    /// </summary>
    public static void DrawHemisphereHeatMap(Canvas canvas, double cx, double cy, double compassRadius,
        double[,] hemiPowers, double elStepDeg, double azStepDeg, Color accentColor, double azStartDeg = 0.0)
    {
        int nEl = hemiPowers.GetLength(0);
        int nAz = hemiPowers.GetLength(1);
        if (nEl < 1 || nAz < 1) return;

        // Normalise
        double minP = double.MaxValue, maxP = double.MinValue;
        for (int e = 0; e < nEl; e++)
            for (int a = 0; a < nAz; a++)
            {
                double v = hemiPowers[e, a];
                if (v < minP) minP = v;
                if (v > maxP) maxP = v;
            }
        double range = maxP - minP;
        if (range < 1e-9) return;

        int bmpSize = (int)(compassRadius * 2.0) + 2;
        if (bmpSize < 2) return;

        int stride = bmpSize * 4;
        byte[] pixels = new byte[bmpSize * stride];
        double bCx = bmpSize * 0.5, bCy = bmpSize * 0.5;
        double azSpanDeg = nAz * azStepDeg;

        for (int py = 0; py < bmpSize; py++)
        {
            for (int px = 0; px < bmpSize; px++)
            {
                double dx = px - bCx, dy = py - bCy;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r > compassRadius) continue;

                // Azimuthal equidistant: r=0 → el=90°, r=compassRadius → el=0°
                double el = 90.0 * (1.0 - r / compassRadius);
                double az = Math.Atan2(-dy, dx) * 180.0 / Math.PI;
                if (az < 0) az += 360.0;
                if (az < azStartDeg || az >= azStartDeg + azSpanDeg) continue; // outside the scanned azimuth range

                int eBin = Math.Min((int)(el / elStepDeg), nEl - 1);
                int aBin = Math.Min((int)((az - azStartDeg) / azStepDeg), nAz - 1);

                double norm = (hemiPowers[eBin, aBin] - minP) / range;
                double normSq = norm * norm; // accentuate peaks
                byte alpha = (byte)(220.0 * normSq);

                int idx = py * stride + px * 4;
                pixels[idx + 0] = accentColor.B;
                pixels[idx + 1] = accentColor.G;
                pixels[idx + 2] = accentColor.R;
                pixels[idx + 3] = alpha;
            }
        }

        var bitmap = new WriteableBitmap(new PixelSize(bmpSize, bmpSize), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer fb = bitmap.Lock())
        {
            if (fb.RowBytes == stride)
            {
                Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
            }
            else
            {
                // Destination has row padding — copy row by row.
                for (int row = 0; row < bmpSize; row++)
                {
                    Marshal.Copy(pixels, row * stride, fb.Address + row * fb.RowBytes, stride);
                }
            }
        }

        var img = new Image { Source = bitmap, Width = bmpSize, Height = bmpSize };
        Canvas.SetLeft(img, cx - bCx);
        Canvas.SetTop(img, cy - bCy);
        canvas.Children.Add(img);
    }

    /// <summary>
    /// Draws a polar plot of the SRP-PHAT coarse power spectrum as a filled polygon.
    /// The plot is scaled to occupy the inner portion of the compass (up to ~55% of compass radius).
    /// </summary>
    public static void DrawSrpSpectrum(Canvas canvas, double cx, double cy, double compassRadius, double[] powers, double stepDeg, Color accentColor, double startDeg = 0.0)
    {
        if (powers == null || powers.Length < 2) return;

        double minPow = double.MaxValue, maxPow = double.MinValue;
        foreach (double p in powers)
        {
            if (p < minPow) minPow = p;
            if (p > maxPow) maxPow = p;
        }
        double range = maxPow - minPow;
        if (range < 1e-9) return;

        double outerR = compassRadius * 0.55;
        double innerR = compassRadius * 0.05;

        var points = new List<Point>(powers.Length + 1);
        for (int i = 0; i < powers.Length; i++)
        {
            double az = startDeg + i * stepDeg;
            double norm = (powers[i] - minPow) / range;
            double r = innerR + norm * (outerR - innerR);
            double rad = az * Math.PI / 180.0;
            points.Add(new Point(cx + r * Math.Cos(rad), cy - r * Math.Sin(rad)));
        }
        // close by repeating the first point
        points.Add(points[0]);

        var fillBrush = new SolidColorBrush(Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B));
        var strokeBrush = new SolidColorBrush(Color.FromArgb(160, accentColor.R, accentColor.G, accentColor.B));

        var poly = new Polygon
        {
            Points = points,
            Fill = fillBrush,
            Stroke = strokeBrush,
            StrokeThickness = 1.0
        };
        canvas.Children.Add(poly);
    }

    /// <summary>Shades the back half (Y &lt; 0) of the compass disc, marking the region excluded from
    /// a linear array's front/back-ambiguous search. Shades the top half (north, Y ≥ 0, azimuth
    /// [0°, 180°)) since the user is assumed south of the array — see SrpPhatLocalizer._searchBinStart.</summary>
    public static void DrawAmbiguityMask(Canvas canvas, double cx, double cy, double radius, IBrush dim)
    {
        var figure = new PathFigure { StartPoint = new Point(cx - radius, cy), IsClosed = true };
        figure.Segments!.Add(new ArcSegment
        {
            Point = new Point(cx + radius, cy),
            Size = new Size(radius, radius),
            RotationAngle = 0.0,
            IsLargeArc = true,
            SweepDirection = SweepDirection.CounterClockwise
        });

        var mask = new Avalonia.Controls.Shapes.Path
        {
            Data = new PathGeometry { Figures = new PathFigures { figure } },
            Fill = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0))
        };
        canvas.Children.Add(mask);
        AddText(canvas, "back (ambiguous)", cx - 44.0, cy - radius * 0.5, dim);
    }

    public static void DrawPairLine(Canvas canvas, double x1, double y1, double x2, double y2, IBrush stroke, bool isUsed)
    {
        var line = new Line
        {
            StartPoint = new Point(x1, y1),
            EndPoint = new Point(x2, y2),
            Stroke = stroke,
            StrokeThickness = isUsed ? 2.5 : 1.5
        };
        if (!isUsed)
            line.StrokeDashArray = new AvaloniaList<double> { 4, 3 };
        canvas.Children.Add(line);
    }

    public static void DrawAzimuthArrow(Canvas canvas, double centerX, double centerY, double radius, double azimuthDeg, IBrush stroke)
    {
        double radians = azimuthDeg * Math.PI / 180.0;
        canvas.Children.Add(new Line
        {
            StartPoint = new Point(centerX, centerY),
            EndPoint = new Point(centerX + radius * Math.Cos(radians), centerY - radius * Math.Sin(radians)),
            Stroke = stroke,
            StrokeThickness = 3.0,
            StrokeLineCap = PenLineCap.Round
        });
    }

    /// <summary>Marks a computed point (e.g. a triangulated source fix) with an X-shaped cross.</summary>
    public static void DrawSourceMarker(Canvas canvas, double x, double y, IBrush stroke)
    {
        const double size = 9.0;
        canvas.Children.Add(new Line { StartPoint = new Point(x - size, y - size), EndPoint = new Point(x + size, y + size), Stroke = stroke, StrokeThickness = 2.5 });
        canvas.Children.Add(new Line { StartPoint = new Point(x - size, y + size), EndPoint = new Point(x + size, y - size), Stroke = stroke, StrokeThickness = 2.5 });
    }

    public static void DrawCenter(Canvas canvas, double x, double y, IBrush fill)
    {
        var marker = new Ellipse
        {
            Width = 10.0,
            Height = 10.0,
            Fill = fill
        };
        Canvas.SetLeft(marker, x - 5.0);
        Canvas.SetTop(marker, y - 5.0);
        canvas.Children.Add(marker);
    }

    public static void AddText(Canvas canvas, string text, double x, double y, IBrush foreground)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            FontFamily = new FontFamily("Consolas")
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        canvas.Children.Add(label);
    }
}
