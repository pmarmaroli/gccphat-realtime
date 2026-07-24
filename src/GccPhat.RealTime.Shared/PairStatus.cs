namespace GccPhat.RealTime;

/// <summary>Health/verdict level shown as a colored dot next to a channel pair or localization
/// readout. Platform-neutral — each UI project maps this to its own Brush type via a converter,
/// using <see cref="PairStatusColors.Get"/> as the single source of truth for the actual colors.</summary>
public enum PairStatus
{
    Good,
    Weak,
    Poor,
    Mono
}

public static class PairStatusColors
{
    public static (byte R, byte G, byte B) Get(PairStatus status) => status switch
    {
        PairStatus.Good => (44, 160, 44),
        PairStatus.Weak => (214, 154, 39),
        PairStatus.Mono => (192, 57, 43),
        _ => (170, 170, 170), // Poor
    };
}
