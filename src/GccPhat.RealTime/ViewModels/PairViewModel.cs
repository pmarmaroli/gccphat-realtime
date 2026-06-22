using System.Windows.Media;
using GccPhat.RealTime.Analysis;
using GccPhat.RealTime.Mvvm;

namespace GccPhat.RealTime.ViewModels;

/// <summary>An active channel pair shown in the list, with its latest live readout.</summary>
public sealed class PairViewModel : ObservableObject
{
    private double _currentDelayMs;
    private double _currentRms;
    private bool _valid;

    public PairViewModel(ChannelPair pair, int paletteIndex)
    {
        Pair = pair;
        PaletteIndex = paletteIndex;
        (byte r, byte g, byte b) = Palette.Get(paletteIndex);
        ColorBrush = new SolidColorBrush(Color.FromRgb(r, g, b));
        ColorBrush.Freeze();
    }

    public ChannelPair Pair { get; }
    public int PaletteIndex { get; }
    public Brush ColorBrush { get; }
    public string Label => Pair.ToString();

    public double CurrentDelayMs
    {
        get => _currentDelayMs;
        set { if (SetProperty(ref _currentDelayMs, value)) OnPropertyChanged(nameof(DelayText)); }
    }

    public double CurrentRms
    {
        get => _currentRms;
        set { if (SetProperty(ref _currentRms, value)) OnPropertyChanged(nameof(RmsText)); }
    }

    public bool Valid
    {
        get => _valid;
        set { if (SetProperty(ref _valid, value)) OnPropertyChanged(nameof(DelayText)); }
    }

    public string DelayText => _valid ? $"{_currentDelayMs,8:F3} ms" : "   --   ";
    public string RmsText => _valid ? $"{_currentRms,8:F3}" : "   --   ";
}
