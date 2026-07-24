namespace GccPhat.RealTime.Audio;

internal sealed class PortAudioCaptureDeviceInfo : IAudioCaptureDeviceInfo
{
    public PortAudioCaptureDeviceInfo(int deviceIndex, string name, int channelCount, int sampleRate)
    {
        DeviceIndex = deviceIndex;
        Name = name;
        ChannelCount = channelCount;
        SampleRate = sampleRate;
    }

    public int DeviceIndex { get; }
    public string Name { get; }
    public string Id => DeviceIndex.ToString();
    public int ChannelCount { get; }
    public int SampleRate { get; }

    /// <summary>PortAudio has no WASAPI-exclusive-mode concept.</summary>
    public bool UseExclusive => false;

    public override string ToString() => $"{Name}  ({ChannelCount} ch, {SampleRate} Hz)";
}

internal sealed class PortAudioRenderDeviceInfo : IAudioRenderDeviceInfo
{
    public PortAudioRenderDeviceInfo(int deviceIndex, string name)
    {
        DeviceIndex = deviceIndex;
        Name = name;
    }

    public int DeviceIndex { get; }
    public string Name { get; }
    public string Id => DeviceIndex.ToString();

    public override string ToString() => Name;
}
