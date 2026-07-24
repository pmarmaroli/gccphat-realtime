using System;
using System.Runtime.InteropServices;
using PortAudioSharp;
using Stream = PortAudioSharp.Stream;

namespace GccPhat.RealTime.Audio;

/// <summary>
/// Plays interleaved float32 PCM through PortAudio. <see cref="Write"/> is push-based (matching
/// <see cref="Analysis.RealTimeEngine"/>'s WOLA beamformer, which produces samples as they're
/// ready), while PortAudio's stream callback is pull-based — a small ring buffer bridges the two,
/// mirroring the role NAudio's BufferedWaveProvider played on Windows.
/// </summary>
public sealed class PortAudioOutputSink : IAudioOutputSink
{
    private readonly int? _deviceIndex;
    private readonly object _lock = new();

    private byte[] _ring = Array.Empty<byte>();
    private int _writePos;
    private int _readPos;
    private int _available;

    private byte[] _callbackScratch = Array.Empty<byte>();
    private int _channels = 1;
    private Stream? _stream;
    private Stream.Callback? _callback;
    private bool _initialized;

    public PortAudioOutputSink(int? deviceIndex)
    {
        _deviceIndex = deviceIndex;
    }

    public void Start(int sampleRateHz, int channels, int blockMs)
    {
        _channels = channels;
        int bytesPerFrame = channels * sizeof(float);
        int bufferMs = blockMs * 4;
        int ringBytes = (int)((long)sampleRateHz * bytesPerFrame * bufferMs / 1000);

        lock (_lock)
        {
            _ring = new byte[Math.Max(ringBytes, bytesPerFrame * 256)];
            _writePos = 0;
            _readPos = 0;
            _available = 0;
        }

        PortAudio.Initialize();
        _initialized = true;

        int deviceIndex = _deviceIndex ?? PortAudio.DefaultOutputDevice;
        DeviceInfo device = PortAudio.GetDeviceInfo(deviceIndex);
        var outParams = new StreamParameters
        {
            device = deviceIndex,
            channelCount = channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = device.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        _callback = OnCallback;
        _stream = new Stream(
            inParams: null,
            outParams: outParams,
            sampleRate: sampleRateHz,
            framesPerBuffer: 0,
            streamFlags: StreamFlags.NoFlag,
            callback: _callback,
            userData: null!);
        _stream.Start();
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_ring.Length == 0)
            {
                return;
            }
            for (int i = 0; i < count; i++)
            {
                if (_available >= _ring.Length)
                {
                    // Overflow: drop the oldest byte to make room, same as
                    // BufferedWaveProvider's DiscardOnBufferOverflow on Windows.
                    _readPos = (_readPos + 1) % _ring.Length;
                    _available--;
                }
                _ring[_writePos] = buffer[offset + i];
                _writePos = (_writePos + 1) % _ring.Length;
                _available++;
            }
        }
    }

    public void Stop()
    {
        try { _stream?.Stop(); }
        catch { /* already stopped */ }
    }

    private StreamCallbackResult OnCallback(IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        int bytesNeeded = (int)frameCount * _channels * sizeof(float);
        if (_callbackScratch.Length < bytesNeeded)
        {
            _callbackScratch = new byte[bytesNeeded];
        }

        lock (_lock)
        {
            int take = Math.Min(_available, bytesNeeded);
            for (int i = 0; i < take; i++)
            {
                _callbackScratch[i] = _ring[_readPos];
                _readPos = (_readPos + 1) % _ring.Length;
            }
            _available -= take;
            if (take < bytesNeeded)
            {
                Array.Clear(_callbackScratch, take, bytesNeeded - take); // underrun: pad with silence
            }
        }

        Marshal.Copy(_callbackScratch, 0, output, bytesNeeded);
        return StreamCallbackResult.Continue;
    }

    public void Dispose()
    {
        Stop();
        _stream?.Dispose();
        _stream = null;
        if (_initialized)
        {
            PortAudio.Terminate();
            _initialized = false;
        }
    }
}
