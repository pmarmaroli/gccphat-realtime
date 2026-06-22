using System;

namespace GccPhat.RealTime.Audio;

/// <summary>
/// Single-channel circular buffer of <see cref="double"/> samples. The capture thread appends
/// blocks via <see cref="WriteBlock"/>; the analysis thread reads the most recent window via
/// <see cref="CopyLatest"/>. Both are guarded by a short lock.
/// </summary>
public sealed class ChannelRingBuffer
{
    private readonly double[] _buffer;
    private readonly object _gate = new();
    private long _written;

    public ChannelRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _buffer = new double[capacity];
    }

    public long TotalWritten
    {
        get { lock (_gate) { return _written; } }
    }

    public void WriteBlock(double[] source, int count)
    {
        lock (_gate)
        {
            int cap = _buffer.Length;
            for (int i = 0; i < count; i++)
            {
                _buffer[(int)(_written % cap)] = source[i];
                _written++;
            }
        }
    }

    /// <summary>Copies the most recent <c>dest.Length</c> samples in chronological order.</summary>
    /// <returns><c>true</c> if enough samples were available, otherwise <c>false</c>.</returns>
    public bool CopyLatest(double[] dest)
    {
        lock (_gate)
        {
            int n = dest.Length;
            if (_written < n)
            {
                return false;
            }
            int cap = _buffer.Length;
            long start = _written - n;
            for (int i = 0; i < n; i++)
            {
                dest[i] = _buffer[(int)((start + i) % cap)];
            }
            return true;
        }
    }
}
