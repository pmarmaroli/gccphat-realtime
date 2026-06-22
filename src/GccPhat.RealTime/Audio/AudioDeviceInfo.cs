using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GccPhat.RealTime.Audio;

/// <summary>Describes a capture endpoint the user can select.</summary>
public sealed class AudioDeviceInfo
{
    public AudioDeviceInfo(MMDevice device, int channelCount, int sampleRate, WaveFormat? nativeFormat, bool useExclusive)
    {
        Device = device;
        ChannelCount = channelCount;
        SampleRate = sampleRate;
        NativeFormat = nativeFormat;
        UseExclusive = useExclusive;
    }

    public MMDevice Device { get; }
    public string Name => Device.FriendlyName;
    public string Id => Device.ID;

    /// <summary>Number of channels actually captured (native count when exclusive mode is required).</summary>
    public int ChannelCount { get; }
    public int SampleRate { get; }

    /// <summary>The device's native (hardware) format, when richer than the shared mix format.</summary>
    public WaveFormat? NativeFormat { get; }

    /// <summary>True when the device exposes more channels natively than in shared mode (needs exclusive capture).</summary>
    public bool UseExclusive { get; }

    public override string ToString()
        => UseExclusive
            ? $"{Name}  ({ChannelCount} ch, {SampleRate} Hz, exclusive)"
            : $"{Name}  ({ChannelCount} ch, {SampleRate} Hz)";
}
