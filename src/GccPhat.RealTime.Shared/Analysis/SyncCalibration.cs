using System;

namespace GccPhat.RealTime.Analysis;

/// <summary>
/// Result of a one-shot "measure sync" action: the apparent delay between Session A's and Session
/// B's independent capture clocks, measured while the two arrays' sync channels were physically
/// touching (so real acoustic delay ~ 0 and the GCC-PHAT delay is ~ pure clock/stream offset).
/// </summary>
/// <param name="OffsetSamples">
/// Signed offset in samples, measured with (channel1 = A's buffer, channel2 = B's buffer) argument
/// order to <c>GccPhatAnalyzer.Estimate</c>. Any later live comparison on this channel pair must
/// reuse that exact (A, B) ordering — swapping it flips the sign and silently corrupts the
/// cross-check.
/// </param>
public sealed record SyncCalibration(
    int ChannelA,
    int ChannelB,
    double OffsetSamples,
    double Coherence,
    DateTime MeasuredAtUtc);
