using System.Collections.Generic;

namespace GccPhat.RealTime.Audio;

/// <summary>WASAPI-backed <see cref="IAudioPlatform"/> for Windows. Falls back to
/// <see cref="PortAudioCapture.FromWdmKsDeviceName"/> (WDM-KS, below any voice-processing APO) when
/// a WASAPI stream reads back silent — see MainViewModel's silence-probe.</summary>
public sealed class WindowsAudioPlatform : IAudioPlatform
{
    public IReadOnlyList<IAudioCaptureDeviceInfo> ListCaptureDevices() => DeviceEnumerator.ListCaptureDevices();

    public IReadOnlyList<IAudioRenderDeviceInfo> ListRenderDevices() => DeviceEnumerator.ListRenderDevices();

    public string? DefaultRenderDeviceId => DeviceEnumerator.GetDefaultRenderDeviceId();

    public ICaptureSource CreateCapture(IAudioCaptureDeviceInfo device, bool forceFallbackBackend)
    {
        if (forceFallbackBackend)
        {
            return PortAudioCapture.FromWdmKsDeviceName(device.Name);
        }

        if (device is not AudioDeviceInfo windowsDevice)
        {
            throw new System.InvalidOperationException($"Device \"{device.Name}\" was not produced by {nameof(WindowsAudioPlatform)}.");
        }
        return new MultichannelCapture(windowsDevice);
    }

    public IAudioOutputSink CreateOutputSink(IAudioRenderDeviceInfo? device) => new WasapiOutputSink(device as RenderDeviceInfo);

    /// <summary>True: some mic-array drivers install a voice-processing APO that silences the
    /// WASAPI stream outright; WDM-KS bypasses it.</summary>
    public bool SupportsSilenceFallback => true;

    public IReadOnlyList<AudioSessionHolder> FindActiveHolders(IAudioCaptureDeviceInfo device)
        => device is AudioDeviceInfo windowsDevice
            ? AudioSessionInspector.FindActiveHolders(windowsDevice.Device)
            : System.Array.Empty<AudioSessionHolder>();

    public IReadOnlyList<AudioSessionHolder> KillAll(IReadOnlyList<AudioSessionHolder> holders) => AudioSessionKiller.KillAll(holders);
}
