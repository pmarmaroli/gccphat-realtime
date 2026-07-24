using System;
using System.Runtime.InteropServices;
using PortAudioSharp;
using Stream = PortAudioSharp.Stream;

namespace GccPhat.RealTime.Audio;

/// <summary>
/// Captures a multichannel array via PortAudio. This is the primary (and only) capture backend on
/// Linux, opened directly from a <see cref="PortAudioPlatform"/>-enumerated device index. On
/// Windows it also serves as the WDM-KS fallback: some mic-array drivers install a voice-processing
/// APO that silences the WASAPI stream outright, and WDM-KS talks straight to the driver, below
/// that APO — see <see cref="FromWdmKsDeviceName"/>.
/// </summary>
public sealed class PortAudioCapture : ICaptureSource
{
    private const int RingCapacity = 1 << 16; // 65536 samples / channel (~1.3 s at 48 kHz)

    private readonly ChannelRingBuffer[] _channels;
    private readonly Stream _stream;
    private readonly Stream.Callback _callback;
    private double[][] _scratch;

    private PortAudioCapture(int deviceIndex)
    {
        DeviceInfo device = PortAudio.GetDeviceInfo(deviceIndex);
        ChannelCount = device.maxInputChannels;
        SampleRate = (int)Math.Round(device.defaultSampleRate);

        _channels = new ChannelRingBuffer[ChannelCount];
        _scratch = new double[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            _channels[c] = new ChannelRingBuffer(RingCapacity);
            _scratch[c] = new double[1024];
        }

        var streamParams = new StreamParameters
        {
            device = deviceIndex,
            channelCount = ChannelCount,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = device.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        _callback = OnCallback;
        _stream = new Stream(
            inParams: streamParams,
            outParams: null,
            sampleRate: SampleRate,
            framesPerBuffer: 0,
            streamFlags: StreamFlags.NoFlag,
            callback: _callback,
            userData: null!);
    }

    /// <summary>Opens the given PortAudio device index directly — the normal path on Linux, where
    /// the index was already resolved by <see cref="PortAudioPlatform.ListCaptureDevices"/>.</summary>
    public static PortAudioCapture FromDeviceIndex(int deviceIndex)
    {
        PortAudio.Initialize();
        try
        {
            return new PortAudioCapture(deviceIndex);
        }
        catch
        {
            PortAudio.Terminate();
            throw;
        }
    }

    /// <summary>Opens the WDM-KS device whose name best matches <paramref name="wantedName"/> —
    /// the Windows fallback path, used when a WASAPI stream reads back silent.</summary>
    public static PortAudioCapture FromWdmKsDeviceName(string wantedName)
    {
        PortAudio.Initialize();
        try
        {
            int deviceIndex = FindWdmKsDeviceIndex(wantedName);
            if (deviceIndex < 0)
            {
                throw new InvalidOperationException($"No WDM-KS device found matching \"{wantedName}\".");
            }
            return new PortAudioCapture(deviceIndex);
        }
        catch
        {
            PortAudio.Terminate();
            throw;
        }
    }

    public int ChannelCount { get; }
    public int SampleRate { get; }

    public ChannelRingBuffer GetChannel(int index) => _channels[index];

    public void Start() => _stream.Start();

    public void Stop()
    {
        try { _stream.Stop(); }
        catch { /* already stopped */ }
    }

    /// <summary>Finds the WDM-KS device whose name best matches <paramref name="wantedName"/>,
    /// preferring the widest channel count among name matches (some drivers expose the same name
    /// on multiple endpoints with different channel counts).</summary>
    private static int FindWdmKsDeviceIndex(string wantedName)
    {
        string wanted = wantedName.Trim();
        int bestIndex = -1;
        int bestChannels = -1;

        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            DeviceInfo device = PortAudio.GetDeviceInfo(i);
            if (device.maxInputChannels <= 0)
            {
                continue;
            }
            if (PaHostApiInterop.GetHostApiType(device.hostApi) != PaHostApiInterop.TypeWdmKs)
            {
                continue;
            }
            string candidate = device.name.Trim();
            bool matches = candidate.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || candidate.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || wanted.Contains(candidate, StringComparison.OrdinalIgnoreCase);
            if (matches && device.maxInputChannels > bestChannels)
            {
                bestIndex = i;
                bestChannels = device.maxInputChannels;
            }
        }

        return bestIndex;
    }

    private StreamCallbackResult OnCallback(IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        if (input == IntPtr.Zero || frameCount == 0)
        {
            return StreamCallbackResult.Continue;
        }

        int frames = (int)frameCount;
        EnsureScratch(frames);

        int total = frames * ChannelCount;
        float[] interleaved = new float[total];
        Marshal.Copy(input, interleaved, 0, total);

        for (int f = 0; f < frames; f++)
        {
            int frameOffset = f * ChannelCount;
            for (int c = 0; c < ChannelCount; c++)
            {
                _scratch[c][f] = interleaved[frameOffset + c];
            }
        }

        for (int c = 0; c < ChannelCount; c++)
        {
            _channels[c].WriteBlock(_scratch[c], frames);
        }

        return StreamCallbackResult.Continue;
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

    public void Dispose()
    {
        Stop();
        _stream?.Dispose();
        PortAudio.Terminate();
    }
}
