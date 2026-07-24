using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GccPhat.RealTime.Audio;

/// <summary>Plays interleaved float32 PCM through WASAPI. Extracted from the beam-listen output
/// path that used to live directly in RealTimeEngine.</summary>
public sealed class WasapiOutputSink : IAudioOutputSink
{
    private readonly MMDevice? _device;
    private WasapiOut? _waveOut;
    private BufferedWaveProvider? _bufferedProvider;

    public WasapiOutputSink(RenderDeviceInfo? renderDevice)
    {
        _device = renderDevice?.Device;
    }

    public void Start(int sampleRateHz, int channels, int blockMs)
    {
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRateHz, channels);
        int bufferMs = blockMs * 4;

        _bufferedProvider = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(bufferMs),
            DiscardOnBufferOverflow = true
        };

        _waveOut = _device is null
            ? new WasapiOut(AudioClientShareMode.Shared, true, blockMs)
            : new WasapiOut(_device, AudioClientShareMode.Shared, true, blockMs);
        _waveOut.Init(_bufferedProvider);
        _waveOut.Play();
    }

    public void Write(byte[] buffer, int offset, int count) => _bufferedProvider?.AddSamples(buffer, offset, count);

    public void Stop()
    {
        _bufferedProvider = null;
        if (_waveOut is null)
        {
            return;
        }

        _waveOut.Stop();
        _waveOut.Dispose();
        _waveOut = null;
    }

    public void Dispose() => Stop();
}
