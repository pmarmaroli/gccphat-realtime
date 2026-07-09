using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace GccPhat.RealTime.Audio;

/// <summary>Finds other processes holding an active (shared-mode) audio session on a capture endpoint.</summary>
public static class AudioSessionInspector
{
    public sealed record Holder(int ProcessId, string ProcessName);

    /// <summary>
    /// Lists distinct external processes with an active session on <paramref name="device"/>.
    /// Only sees shared-mode holders — a rival exclusive-mode client bypasses the audio engine
    /// and creates no session, so it stays invisible to this check.
    /// </summary>
    public static IReadOnlyList<Holder> FindActiveHolders(MMDevice device)
    {
        var holders = new List<Holder>();
        int ownPid = Environment.ProcessId;

        SessionCollection sessions;
        try
        {
            sessions = device.AudioSessionManager.Sessions;
        }
        catch
        {
            return holders;
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            AudioSessionControl session = sessions[i];
            if (session.State != AudioSessionState.AudioSessionStateActive)
            {
                continue;
            }

            int pid = (int)session.GetProcessID;
            if (pid == 0 || pid == ownPid || holders.Exists(h => h.ProcessId == pid))
            {
                continue;
            }

            try
            {
                holders.Add(new Holder(pid, Process.GetProcessById(pid).ProcessName));
            }
            catch (ArgumentException)
            {
                // process exited between enumeration and lookup
            }
        }

        return holders;
    }

    /// <summary>Force-kills the given processes. Returns the ones that could not be killed.</summary>
    public static IReadOnlyList<Holder> KillAll(IEnumerable<Holder> holders)
    {
        var failed = new List<Holder>();
        foreach (Holder holder in holders)
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
