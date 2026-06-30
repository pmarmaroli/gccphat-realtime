using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GccPhat.RealTime.ViewModels;

namespace GccPhat.RealTime;

internal static class ArrayGeometryCanvasDrawing
{
    public static (double CenterX, double CenterY, double Radius) DrawCompass(Canvas canvas, double width, double height, Brush dim)
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

    public static void DrawMicrophone(Canvas canvas, double x, double y, Brush fill, Brush stroke, Brush labelBrush, string label)
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

    public static void DrawAzimuthArrow(Canvas canvas, double centerX, double centerY, double radius, double azimuthDeg, Brush stroke)
    {
        double radians = azimuthDeg * Math.PI / 180.0;
        canvas.Children.Add(new Line
        {
            X1 = centerX,
            Y1 = centerY,
            X2 = centerX + radius * Math.Cos(radians),
            Y2 = centerY - radius * Math.Sin(radians),
            Stroke = stroke,
            StrokeThickness = 3.0,
            StrokeEndLineCap = PenLineCap.Triangle
        });
    }

    public static void DrawCenter(Canvas canvas, double x, double y, Brush fill)
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

    public static void AddText(Canvas canvas, string text, double x, double y, Brush foreground)
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
