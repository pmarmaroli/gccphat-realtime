using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GccPhat.RealTime.Audio;

/// <summary>
/// Captures a multichannel WASAPI endpoint and de-interleaves it into one
/// <see cref="ChannelRingBuffer"/> per channel.
///
/// Uses shared-mode <see cref="WasapiCapture"/> by default, but switches to an exclusive-mode
/// capture (driving <see cref="AudioClient"/> directly) when the device exposes more channels
/// natively than the shared mix format does — which is the usual case for USB microphone arrays
/// that Windows presents as stereo in shared mode.
///
/// A third mode, forced via <c>forcePortAudio</c>, bypasses WASAPI entirely and captures through
/// PortAudio's WDM-KS host API (<see cref="PortAudioWdmKsCapture"/>). This exists because some
/// mic-array drivers install a voice-processing APO that silences the WASAPI stream outright —
/// WDM-KS talks straight to the driver, below that APO. See MainViewModel's silence-probe fallback.
/// </summary>
public sealed class MultichannelCapture : ICaptureSource
{
    private const int RingCapacity = 1 << 16; // 65536 samples / channel (~1.3 s at 48 kHz)

    private readonly ChannelRingBuffer[] _channels;
    private readonly Func<byte[], int, double>? _readSample;
    private readonly int _bytesPerSample;
    private readonly int _blockAlign;
    private readonly bool _exclusive;
    private readonly bool _portAudio;
    private double[][] _scratch;

    // Shared-mode capture.
    private readonly WasapiCapture? _shared;

    // Exclusive-mode capture.
    private readonly AudioClient? _audioClient;
    private readonly AudioCaptureClient? _captureClient;
    private readonly EventWaitHandle? _eventHandle;
    private Thread? _captureThread;
    private volatile bool _running;
    private byte[] _byteScratch = Array.Empty<byte>();

    // WDM-KS (PortAudio) capture.
    private readonly PortAudioWdmKsCapture? _portAudioCapture;

    public MultichannelCapture(AudioDeviceInfo info, bool forcePortAudio = false)
    {
        if (forcePortAudio)
        {
            _portAudio = true;
            _portAudioCapture = new PortAudioWdmKsCapture(info);
            ChannelCount = _portAudioCapture.ChannelCount;
            SampleRate = _portAudioCapture.SampleRate;
            _channels = Array.Empty<ChannelRingBuffer>(); // unused in this mode; GetChannel delegates below
            _scratch = Array.Empty<double[]>();
            return;
        }

        WaveFormat format;
        if (info.UseExclusive && info.NativeFormat is not null)
        {
            _exclusive = true;
            _audioClient = InitializeExclusive(info.Device, info.NativeFormat);
            _eventHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
            _audioClient.SetEventHandle(_eventHandle.SafeWaitHandle.DangerousGetHandle());
            _captureClient = _audioClient.AudioCaptureClient;
            format = info.NativeFormat;
        }
        else
        {
            _exclusive = false;
            _shared = new WasapiCapture(info.Device);
            _shared.DataAvailable += OnSharedDataAvailable;
            format = _shared.WaveFormat;
        }

        ChannelCount = format.Channels;
        SampleRate = format.SampleRate;
        _bytesPerSample = format.BitsPerSample / 8;
        _blockAlign = format.BlockAlign;
        _readSample = PcmSampleReader.BuildReader(format);

        _channels = new ChannelRingBuffer[ChannelCount];
        _scratch = new double[ChannelCount][];
        for (int c = 0; c < ChannelCount; c++)
        {
            _channels[c] = new ChannelRingBuffer(RingCapacity);
            _scratch[c] = new double[1024];
        }
    }

    public int ChannelCount { get; }
    public int SampleRate { get; }

    public ChannelRingBuffer GetChannel(int index) => _portAudio ? _portAudioCapture!.GetChannel(index) : _channels[index];

    public void Start()
    {
        if (_portAudio)
        {
            _portAudioCapture!.Start();
        }
        else if (_exclusive)
        {
            _running = true;
            _audioClient!.Start();
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "WasapiExclusiveCapture" };
            _captureThread.Start();
        }
        else
        {
            _shared!.StartRecording();
        }
    }

    public void Stop()
    {
        if (_portAudio)
        {
            _portAudioCapture!.Stop();
        }
        else if (_exclusive)
        {
            _running = false;
            _captureThread?.Join(500);
            _captureThread = null;
            try { _audioClient?.Stop(); }
            catch { /* already stopped */ }
        }
        else
        {
            try { _shared?.StopRecording(); }
            catch { /* already stopped */ }
        }
    }

    private static AudioClient InitializeExclusive(MMDevice device, WaveFormat format)
    {
        AudioClient client = device.AudioClient;
        long period = client.MinimumDevicePeriod;
        try
        {
            client.Initialize(AudioClientShareMode.Exclusive, AudioClientStreamFlags.EventCallback, period, period, format, Guid.Empty);
        }
        catch (COMException ex) when (unchecked((uint)ex.HResult) == 0x88890019) // AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED
        {
            int alignedFrames = client.BufferSize;
            long alignedPeriod = (long)(10_000_000.0 * alignedFrames / format.SampleRate + 0.5);
            client = device.AudioClient; // a client cannot be re-initialized; get a fresh one
            client.Initialize(AudioClientShareMode.Exclusive, AudioClientStreamFlags.EventCallback, alignedPeriod, alignedPeriod, format, Guid.Empty);
        }
        return client;
    }

    private void CaptureLoop()
    {
        try
        {
            while (_running)
            {
                if (!_eventHandle!.WaitOne(200))
                {
                    continue;
                }

                while (true)
                {
                    IntPtr buffer = _captureClient!.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    if (frames == 0)
                    {
                        break;
                    }

                    int bytes = frames * _blockAlign;
                    if (_byteScratch.Length < bytes)
                    {
                        _byteScratch = new byte[bytes];
                    }

                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                    {
                        Array.Clear(_byteScratch, 0, bytes);
                    }
                    else
                    {
                        Marshal.Copy(buffer, _byteScratch, 0, bytes);
                    }

                    _captureClient.ReleaseBuffer(frames);
                    ProcessBytes(_byteScratch, bytes);
                }
            }
        }
        catch
        {
            // Capture stopped or device removed; exit the loop quietly.
        }
    }

    private void OnSharedDataAvailable(object? sender, WaveInEventArgs e) => ProcessBytes(e.Buffer, e.BytesRecorded);

    private void ProcessBytes(byte[] buffer, int bytesRecorded)
    {
        if (_blockAlign == 0)
        {
            return;
        }
        int frames = bytesRecorded / _blockAlign;
        EnsureScratch(frames);

        for (int f = 0; f < frames; f++)
        {
            int frameOffset = f * _blockAlign;
            for (int c = 0; c < ChannelCount; c++)
            {
                int offset = frameOffset + c * _bytesPerSample;
                _scratch[c][f] = _readSample!(buffer, offset);
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

    public void Dispose()
    {
        Stop();
        if (_shared is not null)
        {
            _shared.DataAvailable -= OnSharedDataAvailable;
            _shared.Dispose();
        }
        _audioClient?.Dispose();
        _eventHandle?.Dispose();
        _portAudioCapture?.Dispose();
    }
}
