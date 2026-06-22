using NAudio.CoreAudioApi;

namespace GccPhat.RealTime.Audio;

/// <summary>Describes a capture endpoint the user can select.</summary>
public sealed class AudioDeviceInfo
{
    public AudioDeviceInfo(MMDevice device, int channelCount, int sampleRate)
    {
        Device = device;
        ChannelCount = channelCount;
        SampleRate = sampleRate;
    }

    public MMDevice Device { get; }
    public string Name => Device.FriendlyName;
    public string Id => Device.ID;
    public int ChannelCount { get; }
    public int SampleRate { get; }

    public override string ToString() => $"{Name}  ({ChannelCount} ch, {SampleRate} Hz)";
}
