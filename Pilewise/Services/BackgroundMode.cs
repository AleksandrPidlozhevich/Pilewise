using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pilewise.Services;

/// <summary>
/// Windows only: while Pilewise sits hidden in the tray it runs in efficiency mode (EcoQoS) at
/// below-normal priority, so a background sync never competes with what the user is doing. Showing
/// the window hands both back to the system. Best effort: older Windows simply ignores it.
/// </summary>
public static class BackgroundMode
{
    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS
    private const uint ExecutionSpeed = 1;         // PROCESS_POWER_THROTTLING_EXECUTION_SPEED

    public static void Set(bool background)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = background ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            // Mask 0 = "let Windows decide" again, rather than forcing high QoS.
            var state = new PowerThrottlingState
            {
                Version = 1,
                ControlMask = background ? ExecutionSpeed : 0,
                StateMask = background ? ExecutionSpeed : 0
            };
            SetProcessInformation(process.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or EntryPointNotFoundException)
        {
            // Not allowed or not available: Pilewise just runs at normal speed.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr process, int informationClass, ref PowerThrottlingState information, int size);
}
