using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GccPhat.RealTime.Audio;

/// <summary>Force-kills processes by PID. Platform-neutral: finding the holders in the first place
/// is what differs per OS (see <see cref="IAudioPlatform.FindActiveHolders"/>).</summary>
public static class AudioSessionKiller
{
    /// <summary>Force-kills the given processes. Returns the ones that could not be killed.</summary>
    public static IReadOnlyList<AudioSessionHolder> KillAll(IEnumerable<AudioSessionHolder> holders)
    {
        var failed = new List<AudioSessionHolder>();
        foreach (AudioSessionHolder holder in holders)
        {
            try
            {
                using Process process = Process.GetProcessById(holder.ProcessId);
                process.Kill();
                process.WaitForExit(2000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                failed.Add(holder);
            }
        }

        return failed;
    }
}
