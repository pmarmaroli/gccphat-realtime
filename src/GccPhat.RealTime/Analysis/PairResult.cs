namespace GccPhat.RealTime.Analysis;

/// <summary>A single real-time delay estimate for one channel pair at a point in time.</summary>
public readonly record struct PairResult(
    ChannelPair Pair,
    double TimeSeconds,
    double DelayMs,
    double Rms,
    double LevelA,
    double LevelB,
    double Coherence,
    bool Valid);
