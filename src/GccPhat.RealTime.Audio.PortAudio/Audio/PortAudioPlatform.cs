using System;
using System.Collections.Generic;
using PortAudioSharp;

namespace GccPhat.RealTime.Audio;

/// <summary>
/// Cross-platform <see cref="IAudioPlatform"/> backed by PortAudio — the only capture/output
/// backend on Linux (native ALSA/PulseAudio host APIs), and the WDM-KS fallback backend on Windows
/// (see <see cref="PortAudioCapture.FromWdmKsDeviceName"/>, used by
/// <c>GccPhat.RealTime.Audio.Windows.WindowsAudioPlatform</c>).
/// </summary>
public sealed class PortAudioPlatform : IAudioPlatform
{
    public IReadOnlyList<IAudioCaptureDeviceInfo> ListCaptureDevices()
    {
        PortAudio.Initialize();
        try
        {
            var result = new List<IAudioCaptureDeviceInfo>();
            for (int i = 0; i < PortAudio.DeviceCount; i++)
            {
                DeviceInfo device = PortAudio.GetDeviceInfo(i);
                if (device.maxInputChannels <= 0)
                {
                    continue;
                }
                result.Add(new PortAudioCaptureDeviceInfo(i, device.name, device.maxInputChannels, (int)Math.Round(device.defaultSampleRate)));
            }
            return result;
        }
        finally
        {
            PortAudio.Terminate();
        }
    }

    public IReadOnlyList<IAudioRenderDeviceInfo> ListRenderDevices()
    {
        PortAudio.Initialize();
        try
        {
            var result = new List<IAudioRenderDeviceInfo>();
            for (int i = 0; i < PortAudio.DeviceCount; i++)
            {
                DeviceInfo device = PortAudio.GetDeviceInfo(i);
                if (device.maxOutputChannels <= 0)
                {
                    continue;
                }
                result.Add(new PortAudioRenderDeviceInfo(i, device.name));
            }
            return result;
        }
        finally
        {
            PortAudio.Terminate();
        }
    }

    public string? DefaultRenderDeviceId
    {
        get
        {
            PortAudio.Initialize();
            try
            {
                int index = PortAudio.DefaultOutputDevice;
                return index >= 0 ? index.ToString() : null;
            }
            finally
            {
                PortAudio.Terminate();
            }
        }
    }

    public ICaptureSource CreateCapture(IAudioCaptureDeviceInfo device, bool forceFallbackBackend)
    {
        if (device is not PortAudioCaptureDeviceInfo paDevice)
        {
            throw new InvalidOperationException($"Device \"{device.Name}\" was not produced by {nameof(PortAudioPlatform)}.");
        }
        return PortAudioCapture.FromDeviceIndex(paDevice.DeviceIndex);
    }

    public IAudioOutputSink CreateOutputSink(IAudioRenderDeviceInfo? device)
    {
        int? deviceIndex = device is PortAudioRenderDeviceInfo paDevice ? paDevice.DeviceIndex : null;
        return new PortAudioOutputSink(deviceIndex);
    }

    /// <summary>PortAudio is already the only backend — there's no separate fallback to retry through.</summary>
    public bool SupportsSilenceFallback => false;

    /// <summary>PortAudio has no session-enumeration concept (unlike WASAPI's COM session API).</summary>
    public IReadOnlyList<AudioSessionHolder> FindActiveHolders(IAudioCaptureDeviceInfo device) => Array.Empty<AudioSessionHolder>();

    public IReadOnlyList<AudioSessionHolder> KillAll(IReadOnlyList<AudioSessionHolder> holders) => AudioSessionKiller.KillAll(holders);
}
