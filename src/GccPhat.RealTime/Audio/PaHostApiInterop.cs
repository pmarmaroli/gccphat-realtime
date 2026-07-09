using System;
using System.Runtime.InteropServices;

namespace GccPhat.RealTime.Audio;

/// <summary>
/// Fills a gap in PortAudioSharp2: it doesn't bind Pa_GetHostApiInfo, so there is no way to tell
/// which host API (WASAPI, WDM-KS, ...) a device belongs to. This talks to the same native
/// portaudio.dll that PortAudioSharp2 already loads, using the stable portaudio.h struct layout.
/// </summary>
internal static class PaHostApiInterop
{
    // Matches PaHostApiTypeId in portaudio.h.
    public const int TypeWdmKs = 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct PaHostApiInfo
    {
        public int structVersion;
        public int type;
        public IntPtr name;
        public int deviceCount;
        public int defaultInputDevice;
        public int defaultOutputDevice;
    }

    [DllImport("portaudio")]
    private static extern IntPtr Pa_GetHostApiInfo(int hostApi);

    /// <summary>Returns the PaHostApiTypeId (see <see cref="TypeWdmKs"/>) for a device's host API index.</summary>
    public static int GetHostApiType(int hostApiIndex)
    {
        IntPtr ptr = Pa_GetHostApiInfo(hostApiIndex);
        if (ptr == IntPtr.Zero)
        {
            return -1;
        }
        var info = Marshal.PtrToStructure<PaHostApiInfo>(ptr);
        return info.type;
    }
}
