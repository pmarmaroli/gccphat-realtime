using System;

namespace GccPhat.RealTime.Audio;

/// <summary>
/// Common surface for anything that feeds multichannel audio into <see cref="ChannelRingBuffer"/>s
/// for <see cref="Analysis.RealTimeEngine"/> to consume — live device capture or file replay.
/// </summary>
public interface ICaptureSource : IDisposable
{
    int ChannelCount { get; }
    int SampleRate { get; }
    ChannelRingBuffer GetChannel(int index);
    void Start();
    void Stop();
}
