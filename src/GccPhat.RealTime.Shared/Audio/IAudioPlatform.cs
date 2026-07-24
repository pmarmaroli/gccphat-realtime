using System;
using System.Collections.Generic;

namespace GccPhat.RealTime.Audio;

/// <summary>Describes a capture endpoint the user can select, independent of the underlying audio API.</summary>
public interface IAudioCaptureDeviceInfo
{
    string Name { get; }
    string Id { get; }
    int ChannelCount { get; }
    int SampleRate { get; }
    bool UseExclusive { get; }
}

/// <summary>Describes an output/render endpoint the beamformer can play to, independent of the underlying audio API.</summary>
public interface IAudioRenderDeviceInfo
{
    string Name { get; }
    string Id { get; }
}

/// <summary>A running audio output stream fed with interleaved float32 PCM.</summary>
public interface IAudioOutputSink : IDisposable
{
    void Start(int sampleRateHz, int channels, int blockMs);
    void Write(byte[] buffer, int offset, int count);
    void Stop();
}

/// <summary>Another process holding an active session on a capture device.</summary>
public sealed record AudioSessionHolder(int ProcessId, string ProcessName);

/// <summary>
/// Abstracts the audio backend (WASAPI on Windows, PortAudio on Linux/cross-platform) so
/// <see cref="Analysis.RealTimeEngine"/> and the ViewModels can be shared across platforms.
/// </summary>
public interface IAudioPlatform
{
    IReadOnlyList<IAudioCaptureDeviceInfo> ListCaptureDevices();
    IReadOnlyList<IAudioRenderDeviceInfo> ListRenderDevices();
    string? DefaultRenderDeviceId { get; }

    ICaptureSource CreateCapture(IAudioCaptureDeviceInfo device, bool forceFallbackBackend);
    IAudioOutputSink CreateOutputSink(IAudioRenderDeviceInfo? device);

    /// <summary>True on Windows, where a WASAPI stream silenced by a driver's voice-processing APO
    /// can be bypassed by restarting capture through WDM-KS. False on Linux, where PortAudio is
    /// already the only backend and there's no separate fallback to retry through.</summary>
    bool SupportsSilenceFallback { get; }

    /// <summary>Other processes holding an active session on <paramref name="device"/>.
    /// Always empty where the platform has no session-enumeration concept (e.g. Linux).</summary>
    IReadOnlyList<AudioSessionHolder> FindActiveHolders(IAudioCaptureDeviceInfo device);

    /// <summary>Force-closes the given processes. Returns the ones that could not be closed.</summary>
    IReadOnlyList<AudioSessionHolder> KillAll(IReadOnlyList<AudioSessionHolder> holders);
}
