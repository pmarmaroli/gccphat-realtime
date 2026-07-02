using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GccPhat.RealTime.ViewModels;

namespace GccPhat.RealTime
{
    public partial class BeamformerWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly List<MicGeometryViewModel> _hookedMicPositions = new();

        public BeamformerWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;
            _viewModel.PropertyChanged += OnVMChanged;
            _viewModel.MicPositions.CollectionChanged += OnMicPositionsChanged;
            HookMicPositionHandlers();
            Loaded += (_, _) => RedrawAzimuthPreview();
        }

        private void OnVMChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.BeamAzimuthDeg)
                or nameof(MainViewModel.BeamPolarPattern)
                or nameof(MainViewModel.PatternFrequencyHz)
                or nameof(MainViewModel.SelectedBeamformerMode))
            {
                RedrawAzimuthPreview();
            }
        }

        private void OnAzimuthPreviewSizeChanged(object sender, SizeChangedEventArgs e) => RedrawAzimuthPreview();

        private void OnMicPositionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            HookMicPositionHandlers();
            RedrawAzimuthPreview();
        }

        private void OnMicPositionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MicGeometryViewModel.Channel)
                || e.PropertyName == nameof(MicGeometryViewModel.IncludeInBeam))
            {
                RedrawAzimuthPreview();
            }
        }

        private void HookMicPositionHandlers()
        {
            UnhookMicPositionHandlers();
            foreach (MicGeometryViewModel pos in _viewModel.MicPositions)
            {
                pos.PropertyChanged += OnMicPositionPropertyChanged;
                _hookedMicPositions.Add(pos);
            }
        }

        private void UnhookMicPositionHandlers()
        {
            foreach (MicGeometryViewModel pos in _hookedMicPositions)
            {
                pos.PropertyChanged -= OnMicPositionPropertyChanged;
            }
            _hookedMicPositions.Clear();
        }

        private void RedrawAzimuthPreview()
        {
            Canvas canvas = AzimuthPreviewCanvas;
            canvas.Children.Clear();

            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;
            if (width < 20 || height < 20)
            {
                return;
            }

            Brush dim = (Brush)FindResource("TextDimBrush");
            Brush text = (Brush)FindResource("TextBrush");
            Brush accent = (Brush)FindResource("AccentBrush");

            (double centerX, double centerY, double radius) = ArrayGeometryCanvasDrawing.DrawCompass(canvas, width, height, dim);
            double scale = ArrayGeometryCanvasDrawing.ComputeGeometryScale(_viewModel.MicPositions, radius);

            if (_viewModel.BeamPolarPattern is double[] pattern && pattern.Length > 0)
            {
                Color accentColor = accent is SolidColorBrush scb ? scb.Color : Colors.Cyan;
                ArrayGeometryCanvasDrawing.DrawSrpSpectrum(canvas, centerX, centerY, radius, pattern, MainViewModel.BeamPolarPatternStepDeg, accentColor);
            }

            foreach (MicGeometryViewModel pos in _viewModel.MicPositions)
            {
                double px = centerX + pos.X * scale;
                double py = centerY - pos.Y * scale;
                Brush fill = pos.IncludeInBeam ? accent : dim;
                Brush stroke = pos.IncludeInBeam ? accent : text;
                string label = pos.Channel is int channel ? channel.ToString() : pos.Name;
                ArrayGeometryCanvasDrawing.DrawMicrophone(canvas, px, py, fill, stroke, text, label);
            }

            ArrayGeometryCanvasDrawing.DrawAzimuthArrow(canvas, centerX, centerY, radius, _viewModel.BeamAzimuthDeg, accent);
            ArrayGeometryCanvasDrawing.DrawCenter(canvas, centerX, centerY, text);
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _viewModel.PropertyChanged -= OnVMChanged;
            _viewModel.MicPositions.CollectionChanged -= OnMicPositionsChanged;
            UnhookMicPositionHandlers();
            base.OnClosed(e);
        }
    }
}
