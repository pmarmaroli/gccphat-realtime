namespace GccPhat.RealTime.Analysis;

/// <summary>An ordered pair of channel indices to cross-correlate (channel A vs channel B).</summary>
public readonly record struct ChannelPair(int ChannelA, int ChannelB)
{
    public (int A, int B) UnorderedKey => ChannelA <= ChannelB
        ? (ChannelA, ChannelB)
        : (ChannelB, ChannelA);

    public override string ToString() => $"Ch{ChannelA} \u2194 Ch{ChannelB}";
}
