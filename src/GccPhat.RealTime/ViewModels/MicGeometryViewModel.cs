using System.Collections.ObjectModel;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime.ViewModels;

/// <summary>
/// One array position (computed from the chosen geometry) and the capture channel assigned to it.
/// Lets the user map physical mic positions to device channels for SRP-PHAT localization.
/// </summary>
public sealed class MicGeometryViewModel : ObservableObject
{
    private int? _channel;
    private bool _includeInBeam = true;
    private readonly string _name;

    public MicGeometryViewModel(int positionIndex, double x, double y, ObservableCollection<int> channels, string? name = null)
    {
        PositionIndex = positionIndex;
        X = x;
        Y = y;
        AvailableChannels = channels;
        _name = name ?? $"Pos {positionIndex}";
        _channel = positionIndex;
    }

    public int PositionIndex { get; }
    public double X { get; }
    public double Y { get; }
    public ObservableCollection<int> AvailableChannels { get; }

    public string Name => _name;
    public string CoordText => $"({X * 100:F1}, {Y * 100:F1}) cm";

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
