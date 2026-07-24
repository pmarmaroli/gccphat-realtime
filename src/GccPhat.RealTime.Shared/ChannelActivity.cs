namespace GccPhat.RealTime;

/// <summary>Whether a channel meter bar shows idle noise-floor or active signal. Platform-neutral —
/// each UI project maps this to its own Brush type via a converter.</summary>
public enum ChannelActivity
{
    Idle,
    Active
}

public static class ChannelActivityColors
{
    public static (byte R, byte G, byte B) Get(ChannelActivity activity) => activity switch
    {
        ChannelActivity.Active => (44, 160, 44),
        _ => (120, 144, 156), // Idle
    };
}
