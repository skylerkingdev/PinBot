using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PinBot.Agent;

/// <summary>The few Windows calls that keep PinBot light on a cabinet.</summary>
public static class Native
{
    /// <summary>
    /// Process ids of running VPinball*.exe. Listing processes costs a few milliseconds however it is done (Windows
    /// gathers every process's details; a Toolhelp snapshot and the raw system call measured no cheaper), so PinBot
    /// does it rarely: every 5 s while idle, never during play, and before touching a competition file.
    /// </summary>
    public static List<int> VpxProcessIds()
    {
        var ids = new List<int>();
        foreach (var p in Process.GetProcesses())
        {
            if (p.ProcessName.StartsWith("VPinball", StringComparison.OrdinalIgnoreCase)) ids.Add(p.Id);
            p.Dispose();
        }
        return ids;
    }

    /// <summary>
    /// Background citizen: below-normal priority, and Windows 11 "efficiency mode" (EcoQoS: efficient cores, low clock,
    /// no raised timer resolution). PinBot can then never take CPU time VPX wants. Best effort on older Windows.
    /// </summary>
    public static void RunAsBackground()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        var state = new PowerThrottlingState { Version = 1, ControlMask = ExecutionSpeed | IgnoreTimerResolution, StateMask = ExecutionSpeed | IgnoreTimerResolution };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    /// <summary>After a burst of work (startup, a finished game): give memory back to Windows.</summary>
    public static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    const int ProcessPowerThrottling = 4;
    const uint ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
}
