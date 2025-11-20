using System;
using System.Runtime.InteropServices;

namespace OverlayApp.Infrastructure;

internal static class DisplayAffinityHelper
{
    private const uint WdaNone = 0x0;
    private const uint WdaExcludeFromCapture = 0x11;

    public static bool TryExcludeFromCapture(IntPtr hwnd, ILogger? logger)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            logger?.Log("DisplayAffinity", "Exclude-from-capture requires Windows 10 version 2004 or later.");
            return false;
        }

        try
        {
            if (!SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
            {
                var error = Marshal.GetLastWin32Error();
                logger?.Log("DisplayAffinity", $"SetWindowDisplayAffinity failed (error {error}).");
                return false;
            }

            logger?.Log("DisplayAffinity", "Overlay window excluded from capture.");
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            logger?.Log("DisplayAffinity", "SetWindowDisplayAffinity is not available on this OS build.");
            return false;
        }
    }

    public static void ClearAffinity(IntPtr hwnd, ILogger? logger)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!SetWindowDisplayAffinity(hwnd, WdaNone))
            {
                var error = Marshal.GetLastWin32Error();
                logger?.Log("DisplayAffinity", $"Failed to reset display affinity (error {error}).");
            }
        }
        catch (EntryPointNotFoundException)
        {
            // API not present; nothing to clear.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
