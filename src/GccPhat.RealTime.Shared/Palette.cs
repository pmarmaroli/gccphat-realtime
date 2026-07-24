namespace GccPhat.RealTime;

/// <summary>Shared colour palette so the pair list swatches and the plot lines match.</summary>
public static class Palette
{
    public static readonly (byte R, byte G, byte B)[] Colors =
    {
        (31, 119, 180),   // blue
        (255, 127, 14),   // orange
        (44, 160, 44),    // green
        (214, 39, 40),    // red
        (148, 103, 189),  // purple
        (140, 86, 75),    // brown
        (227, 119, 194),  // pink
        (127, 127, 127),  // grey
    };

    public static (byte R, byte G, byte B) Get(int index) => Colors[index % Colors.Length];
}
