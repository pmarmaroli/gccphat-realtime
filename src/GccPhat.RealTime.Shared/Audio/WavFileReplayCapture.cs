using System;
using System.Diagnostics;
using System.Threading;
using NAudio.Wave;

namespace GccPhat.RealTime.Audio;

/// <summary>
/// Replays a multichannel WAV file into the same <see cref="ChannelRingBuffer"/> mechanism used by
/// live capture, at real-time pace, so <see cref="Analysis.RealTimeEngine"/> and every downstream
/// panel behave identically to a live session ("conditions du direct").
/// </summary>
public sealed class WavFileReplayCapture : ICaptureSource
{
    private const int RingCapacity = 1 << 18; // matches MultichannelCapture / PortAudioCapture
    private const int BlockFrames = 1024;

    private readonly ChannelRingBuffer[] _channels;
    private readonly double[] _interleaved; // [frame * ChannelCount + channel]
    private readonly int _totalFrames;
    private readonly double[][] _scratch;

    private Thread? _pumpThread;
    private volatile bool _running;
    private volatile int _currentFrame;

    /// <summary>
    /// Raised on the pump thread exactly once when the file is exhausted and pumping stops on its
    /// own — never raised on a user-initiated <see cref="Stop"/>/<see cref="Dispose"/>. Subscribers
    /// must marshal to the UI thread.
    /// </summary>
    public event Action? PlaybackFinished;

    public WavFileReplayCapture(string filePath)
    {
        using var reader = new WaveFileReader(filePath);
        WaveFormat format = reader.WaveFormat;
        ChannelCount = format.Channels;
        SampleRate = format.SampleRate;

        Func<byte[], int, double> readSample = PcmSampleReader.BuildReader(format);
        int blockAlign = format.BlockAlign;
        int bytesPerSample = format.BitsPerSample / 8;

        byte[] raw = new byte[reader.Length];
        int read = reader.Read(raw, 0, raw.Length);
        _totalFrames = blockAlign == 0 ? 0 : read / blockAlign;

        _interleaved = new double[(long)_totalFrames * ChannelCount];
        for (int f = 0; f < _totalFrames; f++)
        {
            int frameOffset = f * blockAlign;
            for (int c = 0; c < ChannelCount; c++)
            {
                int offset = frameOffset + c * bytesPerSample;
                _interleaved[f * ChannelCount + c] = readSample(raw, offset);
            }
        }

        _channels = new ChannelRingBuffer[ChannelCount];
        _scratch = new double[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            _channels[c] = new ChannelRingBuffer(RingCapacity);
            _scratch[c] = new double[BlockFrames];
        }
    }

    public int ChannelCount { get; }
    public int SampleRate { get; }

    /// <summary>Total length of the loaded file.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(SampleRate == 0 ? 0 : _totalFrames / (double)SampleRate);

    /// <summary>Playback position of the last block written to the ring buffers.</summary>
    public TimeSpan Position => TimeSpan.FromSeconds(SampleRate == 0 ? 0 : _currentFrame / (double)SampleRate);

    public ChannelRingBuffer GetChannel(int index) => _channels[index];

    public void Start()
    {
        _running = true;
        _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "WavReplayPump" };
        _pumpThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _pumpThread?.Join(500);
        _pumpThread = null;
    }

    private void PumpLoop()
    {
        var sw = Stopwatch.StartNew();
        double blockDurationMs = 1000.0 * BlockFrames / SampleRate;
        double nextDueMs = 0;
        int frame = 0;

        while (_running && frame < _totalFrames)
        {
            int n = Math.Min(BlockFrames, _totalFrames - frame);
            for (int c = 0; c < ChannelCount; c++)
            {
                double[] scratch = _scratch[c];
                for (int i = 0; i < n; i++)
                {
                    scratch[i] = _interleaved[(frame + i) * ChannelCount + c];
                }
                _channels[c].WriteBlock(scratch, n);
            }

            frame += n;
            _currentFrame = frame;
            nextDueMs += blockDurationMs * n / BlockFrames;
            double waitMs = nextDueMs - sw.Elapsed.TotalMilliseconds;
            if (waitMs > 0)
            {
                Thread.Sleep((int)waitMs);
            }
        }

        if (_running)
        {
            _running = false;
            PlaybackFinished?.Invoke();
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
