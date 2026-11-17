using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TaskTimer.Services;

/// <summary>
/// Detects an active Teams call or meeting. Two independent signals are checked, since each one is
/// missing in some setups:
/// <list type="number">
/// <item>Windows' microphone usage record (CapabilityAccessManager consent store): LastUsedTimeStop is 0
/// while an app still holds the microphone. It stays empty when microphone access for apps is turned
/// off in the privacy settings, and for some Teams versions.</item>
/// <item>The audio streams open right now (Windows Core Audio): an active microphone stream belonging
/// to a Teams process. Teams keeps it open for the whole call, even while muted.</item>
/// </list>
/// Neither is seen on a remote desktop where Teams sends the call's audio to the user's own computer
/// (Teams optimized for VDI/AVD); there the call happens on the other computer.
/// </summary>
public static class TeamsCallDetector
{
    private const string MicKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public static bool IsInCall() => MicrophoneRecordInUse() || AudioStreamActive();

    /// <summary>What each signal sees now, for the "Test call detection" button.</summary>
    public static string Describe()
    {
        var lines = new List<string>
        {
            Loc.T("DetectRecord", Loc.T(MicrophoneRecordInUse() ? "DetectYes" : "DetectNo")),
            Loc.T("DetectStream", Loc.T(AudioStreamActive() ? "DetectYes" : "DetectNo"))
        };
        var processes = TeamsProcessIds().Count;
        lines.Add(processes > 0 ? Loc.T("DetectTeamsRunning", processes) : Loc.T("DetectTeamsNotRunning"));
        if (AppsMicrophoneDenied()) lines.Add(Loc.T("DetectMicDenied"));
        if (SystemInformation.TerminalServerSession) lines.Add(Loc.T("DetectRemote"));
        return string.Join(Environment.NewLine, lines);
    }

    // ---------- 1. Windows' microphone usage record ----------

    private static bool MicrophoneRecordInUse()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(MicKey);
            if (root == null) return false;
            if (AnyTeamsAppInUse(root)) return true;
            using var nonPackaged = root.OpenSubKey("NonPackaged");
            return nonPackaged != null && AnyTeamsAppInUse(nonPackaged);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool AnyTeamsAppInUse(RegistryKey parent)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            // Packaged: "MSTeams_8wekyb3d8bbwe"; classic: "...#Teams.exe" / "...#ms-teams.exe".
            if (!name.Contains("teams", StringComparison.OrdinalIgnoreCase)) continue;
            using var app = parent.OpenSubKey(name);
            if (app?.GetValue("LastUsedTimeStart") is long start && start != 0 &&
                app.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                return true;
        }
        return false;
    }

    /// <summary>"Let apps access your microphone" is off, so Windows keeps no usage record for Teams.</summary>
    private static bool AppsMicrophoneDenied()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(MicKey);
            return string.Equals(root?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    // ---------- 2. Active audio streams (Core Audio) ----------

    /// <summary>IDs of running Teams processes: new Teams (ms-teams, its media host) and classic Teams.</summary>
    private static HashSet<int> TeamsProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                if (process.ProcessName.Contains("teams", StringComparison.OrdinalIgnoreCase)) ids.Add(process.Id);
        }
        return ids;
    }

    /// <summary>Whether a Teams process has an active stream on any microphone.</summary>
    private static bool AudioStreamActive()
    {
        var teams = TeamsProcessIds();
        if (teams.Count == 0) return false;

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(EDataFlowCapture, DeviceStateActive, out devices) != 0 || devices == null) return false;
            devices.GetCount(out var count);
            for (uint i = 0; i < count; i++)
                if (DeviceHasTeamsStream(devices, i, teams)) return true;
            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false; // no audio service or devices (e.g. a server without sound)
        }
        finally
        {
            Release(devices);
            Release(enumerator);
        }
    }

    private static bool DeviceHasTeamsStream(IMMDeviceCollection devices, uint index, HashSet<int> teams)
    {
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            if (devices.Item(index, out device) != 0 || device == null) return false;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var activated) != 0) return false;
            manager = (IAudioSessionManager2)activated;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null) return false;
            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                IAudioSessionControl2? session = null;
                try
                {
                    if (sessions.GetSession(i, out session) != 0 || session == null) continue;
                    session.GetState(out var state);
                    session.GetProcessId(out var pid);
                    if (state == AudioSessionStateActive && teams.Contains((int)pid)) return true;
                }
                finally
                {
                    Release(session);
                }
            }
            return false;
        }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(device);
        }
    }

    // Checked every few seconds for hours, so every COM object is released straight away.
    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    private const int EDataFlowCapture = 1;
    private const uint DeviceStateActive = 1;
    private const int ClsCtxAll = 23;
    private const int AudioSessionStateActive = 1;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        // Further methods are not called; they need no declaration after the ones used.
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint processId);
    }
}
