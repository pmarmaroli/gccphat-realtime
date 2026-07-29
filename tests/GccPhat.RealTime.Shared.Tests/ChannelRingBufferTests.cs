using GccPhat.RealTime.Audio;

namespace GccPhat.RealTime.Shared.Tests;

public class ChannelRingBufferTests
{
    private const int Capacity = 65536;

    /// <summary>Fills a ring with a strictly increasing ramp, so any discontinuity is detectable.</summary>
    private static ChannelRingBuffer FilledRamp(int capacity, int totalSamples)
    {
        var ring = new ChannelRingBuffer(capacity);
        var block = new double[1000];
        long n = 0;
        while (n < totalSamples)
        {
            for (int i = 0; i < block.Length; i++) block[i] = n++;
            ring.WriteBlock(block, block.Length);
        }
        return ring;
    }

    /// <summary>
    /// Regression test. The classifier asks for one second of audio, which at 96 kHz is 96000
    /// samples - more than the ring used to hold. CopyLatest only checked `_written &lt; n`, never
    /// the capacity, so it returned true after wrapping the ring ~1.5x and re-reading the same
    /// slots: real audio, but temporally scrambled. It must refuse instead.
    ///
    /// Against the pre-fix code this scenario returns true, and the "contiguous" window jumps
    /// backwards mid-stream - with a 200000-sample ramp written, sample 30464 steps from
    /// 199999 back to 134464. That is what YAMNet was being fed at 96 kHz.
    /// </summary>
    [Fact]
    public void CopyLatest_RefusesReadLargerThanCapacity()
    {
        ChannelRingBuffer ring = FilledRamp(Capacity, 200_000);

        var oneSecondAt96kHz = new double[96_000];

        Assert.False(ring.CopyLatest(oneSecondAt96kHz));
    }

    [Fact]
    public void CopyLatest_ReturnsContiguousMostRecentSamples()
    {
        const int total = 200_000;
        ChannelRingBuffer ring = FilledRamp(Capacity, total);

        var dest = new double[48_000]; // one second at 48 kHz
        Assert.True(ring.CopyLatest(dest));

        // Chronological order, no gaps or repeats.
        for (int i = 1; i < dest.Length; i++)
        {
            Assert.Equal(1.0, dest[i] - dest[i - 1]);
        }

        // Ends on the newest sample written.
        Assert.Equal(total - 1, dest[^1]);
    }

    [Fact]
    public void CopyLatest_AllowsReadOfExactlyCapacity()
    {
        ChannelRingBuffer ring = FilledRamp(Capacity, 200_000);

        var dest = new double[Capacity];

        Assert.True(ring.CopyLatest(dest));
        Assert.Equal(200_000 - 1, dest[^1]);
    }

    [Fact]
    public void CopyLatest_ReturnsFalseBeforeEnoughSamplesExist()
    {
        ChannelRingBuffer ring = FilledRamp(Capacity, 1_000);

        var dest = new double[48_000];

        Assert.False(ring.CopyLatest(dest));
    }

    [Fact]
    public void CopyRange_RefusesRangeAlreadyOverwritten()
    {
        ChannelRingBuffer ring = FilledRamp(Capacity, 200_000);

        var dest = new double[1_000];

        Assert.False(ring.CopyRange(0, dest));            // long gone from the ring
        Assert.False(ring.CopyRange(199_999, dest));      // runs past what has been written
        Assert.True(ring.CopyRange(199_000, dest));       // still resident
        Assert.Equal(199_000, dest[0]);
    }
}
