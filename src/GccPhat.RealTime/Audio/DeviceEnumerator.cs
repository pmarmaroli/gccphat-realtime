using System.Collections.Generic;
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
            int channels = 2;
            int sampleRate = 48000;
            try
            {
                WaveFormat mix = device.AudioClient.MixFormat;
                channels = mix.Channels;
                sampleRate = mix.SampleRate;
            }
            catch
            {
                // Some endpoints refuse activation while in use; fall back to defaults.
            }

            result.Add(new AudioDeviceInfo(device, channels, sampleRate));
        }

        return result;
    }
}
