using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GccPhat.RealTime.Audio;

/// <summary>Enumerates active WASAPI capture endpoints (microphone arrays, internal mics, ...).</summary>
public static class DeviceEnumerator
{
    public static IReadOnlyList<AudioDeviceInfo> ListCaptureDevices()
    {
        var result = new List<AudioDeviceInfo>();
        var enumerator = new MMDeviceEnumerator();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            int mixChannels = 2;
            int mixRate = 48000;
            try
            {
                WaveFormat mix = device.AudioClient.MixFormat;
                mixChannels = mix.Channels;
                mixRate = mix.SampleRate;
            }
            catch
            {
                // Some endpoints refuse activation while in use; fall back to defaults.
            }

            WaveFormat? nativeFormat = TryReadNativeFormat(device);

            // The shared mix format is often forced to stereo even for multichannel arrays.
            // When the device's native format exposes more channels, capture it in exclusive mode.
            bool useExclusive = nativeFormat is not null && nativeFormat.Channels > mixChannels;
            int channels = useExclusive ? nativeFormat!.Channels : mixChannels;
            int rate = useExclusive ? nativeFormat!.SampleRate : mixRate;

            result.Add(new AudioDeviceInfo(device, channels, rate, nativeFormat, useExclusive));
        }

        return result;
    }

    /// <summary>Reads the device's native (hardware) format from its property store, if available.</summary>
    private static WaveFormat? TryReadNativeFormat(MMDevice device)
    {
        try
        {
            PropertyKey key = PropertyKeys.PKEY_AudioEngine_DeviceFormat;
            if (!device.Properties.Contains(key))
            {
                return null;
            }

            if (device.Properties[key].Value is byte[] blob && blob.Length >= 18)
            {
                GCHandle handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
                try
                {
                    return WaveFormat.MarshalFromPtr(handle.AddrOfPinnedObject());
                }
                finally
                {
                    handle.Free();
                }
            }
        }
        catch
        {
            // Property unavailable or malformed; treat as "no native format".
        }

        return null;
    }
}
