using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GccPhat.RealTime.Audio;

/// <summary>
/// Captures a multichannel WASAPI endpoint and de-interleaves it into one
/// <see cref="ChannelRingBuffer"/> per channel.
/// </summary>
public sealed class MultichannelCapture : IDisposable
{
    private const int RingCapacity = 1 << 16; // 65536 samples / channel (~1.3 s at 48 kHz)

    private readonly WasapiCapture _capture;
    private readonly ChannelRingBuffer[] _channels;
    private readonly Func<byte[], int, double> _readSample;
    private readonly int _bytesPerSample;
    private double[][] _scratch;

    public MultichannelCapture(MMDevice device)
    {
        _capture = new WasapiCapture(device);
        WaveFormat format = _capture.WaveFormat;
        ChannelCount = format.Channels;
        SampleRate = format.SampleRate;
        _bytesPerSample = format.BitsPerSample / 8;

        _channels = new ChannelRingBuffer[ChannelCount];
        _scratch = new double[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            _channels[c] = new ChannelRingBuffer(RingCapacity);
            _scratch[c] = new double[1024];
        }

        _readSample = BuildSampleReader(format);
        _capture.DataAvailable += OnDataAvailable;
    }

    public int ChannelCount { get; }
    public int SampleRate { get; }

    public ChannelRingBuffer GetChannel(int index) => _channels[index];

    public void Start() => _capture.StartRecording();

    public void Stop()
    {
        try { _capture.StopRecording(); }
        catch { /* already stopped */ }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int frameSize = _bytesPerSample * ChannelCount;
        if (frameSize == 0)
        {
            return;
        }
        int frames = e.BytesRecorded / frameSize;
        EnsureScratch(frames);

        for (int f = 0; f < frames; f++)
        {
            int frameOffset = f * frameSize;
            for (int c = 0; c < ChannelCount; c++)
            {
                int offset = frameOffset + c * _bytesPerSample;
                _scratch[c][f] = _readSample(e.Buffer, offset);
            }
        }

        for (int c = 0; c < ChannelCount; c++)
        {
            _channels[c].WriteBlock(_scratch[c], frames);
        }
    }

    private void EnsureScratch(int frames)
    {
        if (_scratch[0].Length >= frames)
        {
            return;
        }
        int size = _scratch[0].Length;
        while (size < frames)
        {
            size <<= 1;
        }
        for (int c = 0; c < ChannelCount; c++)
        {
            _scratch[c] = new double[size];
        }
    }

    private static Func<byte[], int, double> BuildSampleReader(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            return static (buf, off) => BitConverter.ToSingle(buf, off);
        }
        if (format.Encoding == WaveFormatEncoding.Pcm)
        {
            switch (format.BitsPerSample)
            {
                case 16:
                    return static (buf, off) => BitConverter.ToInt16(buf, off) / 32768.0;
                case 24:
                    return static (buf, off) =>
                    {
                        int sample = (buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16));
                        if ((sample & 0x800000) != 0)
                        {
                            sample |= unchecked((int)0xFF000000);
                        }
                        return sample / 8388608.0;
                    };
                case 32:
                    return static (buf, off) => BitConverter.ToInt32(buf, off) / 2147483648.0;
            }
        }

        throw new NotSupportedException(
            $"Unsupported capture format: {format.Encoding} {format.BitsPerSample}-bit.");
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        Stop();
        _capture.Dispose();
    }
}
