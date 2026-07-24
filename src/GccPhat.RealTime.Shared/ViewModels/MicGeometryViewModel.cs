using System;
using System.Collections.ObjectModel;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime.ViewModels;

/// <summary>
/// One array position and the capture channel assigned to it. For Circular/Linear layouts, X/Y are
/// computed and read-only in practice (never assigned to after construction). For the Custom layout,
/// X/Y/Z are freely user-editable, either directly (cartesian) or via the Azimuth/Elevation/Radius
/// fields (spherical) — <see cref="ApplyCartesian"/>/<see cref="ApplySpherical"/> keep both
/// representations in sync when the array-wide input mode is toggled. Z is metadata only: SRP-PHAT
/// localization is 2D and only ever reads X/Y.
/// </summary>
public sealed class MicGeometryViewModel : ObservableObject
{
    private int? _channel;
    private bool _includeInBeam = true;
    private readonly string _name;

    private double _x;
    private double _y;
    private double _z;
    private double _azimuthDeg;
    private double _elevationDeg;
    private double _radiusM;

    public MicGeometryViewModel(int positionIndex, double x, double y, ObservableCollection<int> channels, string? name = null, double z = 0)
    {
        PositionIndex = positionIndex;
        _x = x;
        _y = y;
        _z = z;
        AvailableChannels = channels;
        _name = name ?? $"Pos {positionIndex}";
        _channel = positionIndex;
        ApplyCartesian();
    }

    public int PositionIndex { get; }
    public ObservableCollection<int> AvailableChannels { get; }

    public string Name => _name;
    public string CoordText => $"({XMm}, {YMm}, {ZMm}) mm";

    public double X
    {
        get => _x;
        set { if (SetProperty(ref _x, value)) { OnPropertyChanged(nameof(CoordText)); OnPropertyChanged(nameof(XMm)); } }
    }

    public double Y
    {
        get => _y;
        set { if (SetProperty(ref _y, value)) { OnPropertyChanged(nameof(CoordText)); OnPropertyChanged(nameof(YMm)); } }
    }

    public double Z
    {
        get => _z;
        set { if (SetProperty(ref _z, value)) { OnPropertyChanged(nameof(CoordText)); OnPropertyChanged(nameof(ZMm)); } }
    }

    /// <summary>Cartesian X in whole millimetres — convenience proxy for XAML text entry.</summary>
    public int XMm
    {
        get => (int)Math.Round(X * 1000.0);
        set => X = value / 1000.0;
    }

    /// <summary>Cartesian Y in whole millimetres — convenience proxy for XAML text entry.</summary>
    public int YMm
    {
        get => (int)Math.Round(Y * 1000.0);
        set => Y = value / 1000.0;
    }

    /// <summary>Cartesian Z in whole millimetres — convenience proxy for XAML text entry.</summary>
    public int ZMm
    {
        get => (int)Math.Round(Z * 1000.0);
        set => Z = value / 1000.0;
    }

    public double AzimuthDeg
    {
        get => _azimuthDeg;
        set => SetProperty(ref _azimuthDeg, value);
    }

    public double ElevationDeg
    {
        get => _elevationDeg;
        set => SetProperty(ref _elevationDeg, value);
    }

    /// <summary>Spherical radius in whole millimetres — convenience proxy for XAML text entry.</summary>
    public int RadiusMm
    {
        get => (int)Math.Round(_radiusM * 1000.0);
        set => _radiusM = value / 1000.0;
    }

    /// <summary>Recomputes the spherical fields (Azimuth/Elevation/Radius) from the current X/Y/Z.</summary>
    public void ApplyCartesian()
    {
        double radius = Math.Sqrt(_x * _x + _y * _y + _z * _z);
        _radiusM = radius;
        _azimuthDeg = Math.Atan2(_y, _x) * 180.0 / Math.PI;
        _elevationDeg = radius < 1e-9 ? 0.0 : Math.Asin(Math.Clamp(_z / radius, -1.0, 1.0)) * 180.0 / Math.PI;
        OnPropertyChanged(nameof(AzimuthDeg));
        OnPropertyChanged(nameof(ElevationDeg));
        OnPropertyChanged(nameof(RadiusMm));
    }

    /// <summary>Recomputes X/Y/Z from the current Azimuth/Elevation/Radius fields.</summary>
    public void ApplySpherical()
    {
        double azRad = _azimuthDeg * Math.PI / 180.0;
        double elRad = _elevationDeg * Math.PI / 180.0;
        X = _radiusM * Math.Cos(elRad) * Math.Cos(azRad);
        Y = _radiusM * Math.Cos(elRad) * Math.Sin(azRad);
        Z = _radiusM * Math.Sin(elRad);
    }

    /// <summary>Capture channel mounted at this position (null = unassigned).</summary>
    public int? Channel
    {
        get => _channel;
        set => SetProperty(ref _channel, value);
    }

    /// <summary>Whether this microphone contributes to the beamformer sum.</summary>
    public bool IncludeInBeam
    {
        get => _includeInBeam;
        set => SetProperty(ref _includeInBeam, value);
    }
}
