using NAudio.CoreAudioApi;

namespace GccPhat.RealTime.Audio;

/// <summary>Describes an output/render endpoint the beamformer can play to.</summary>
public sealed class RenderDeviceInfo
{
    public RenderDeviceInfo(MMDevice device)
    {
        Device = device;
    }

    public MMDevice Device { get; }
    public string Name => Device.FriendlyName;
    public string Id => Device.ID;

    public override string ToString() => Name;
}
