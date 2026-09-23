using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace OptiscalerClient.Helpers;

/// <summary>
/// Windows only. An undecorated window (SystemDecorations="None") maximizes to the monitor's work
/// area (Avalonia re-positions it there with SetWindowPos), which with an auto-hide taskbar is the
/// whole monitor: Explorer then treats the window as a fullscreen app and never slides the taskbar
/// back in. Stopping 1px short of the edge the auto-hide taskbar lives on (what browsers do too)
/// keeps the taskbar reachable.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsAutoHideTaskbarHelper
{
    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint ABM_GETAUTOHIDEBAREX = 0x0000000b;
    private const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;

    // Never marks the message handled: only edits the proposed rect, Avalonia/DefWindowProc go on as usual.
    public static void Attach(Window window) =>
        Win32Properties.AddWndProcHookCallback(window, (IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_WINDOWPOSCHANGING && IsZoomed(hwnd))
                ClampToAutoHideArea(hwnd, lParam);
            return IntPtr.Zero;
        });

    private static void ClampToAutoHideArea(IntPtr hwnd, IntPtr lParam)
    {
        var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((pos.flags & (SWP_NOSIZE | SWP_NOMOVE)) != 0) return;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;

        var area = info.rcWork;
        var hasAutoHide = false;
        if (HasAutoHideBar(ABE_LEFT, info.rcMonitor)) { area.Left += 1; hasAutoHide = true; }
        if (HasAutoHideBar(ABE_TOP, info.rcMonitor)) { area.Top += 1; hasAutoHide = true; }
        if (HasAutoHideBar(ABE_RIGHT, info.rcMonitor)) { area.Right -= 1; hasAutoHide = true; }
        if (HasAutoHideBar(ABE_BOTTOM, info.rcMonitor)) { area.Bottom -= 1; hasAutoHide = true; }
        if (!hasAutoHide) return;

        pos.x = area.Left;
        pos.y = area.Top;
        pos.cx = area.Right - area.Left;
        pos.cy = area.Bottom - area.Top;
        Marshal.StructureToPtr(pos, lParam, false);
    }

    private static bool HasAutoHideBar(uint edge, RECT monitorRect)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), uEdge = edge, rc = monitorRect };
        return SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref data) != IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
}
