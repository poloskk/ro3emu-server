using System.Runtime.InteropServices;

namespace Ro3Emu.Play;

/// <summary>Window plumbing: dragging the frameless window, rounded corners, bringing a window forward.</summary>
internal static class NativeMethods
{
    internal const int SwRestore = 9;
    private const int WmNcLButtonDown = 0xA1, HtCaption = 2;
    private const int DwmWindowCornerPreference = 33, DwmCornerRound = 2;

    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Starts a window drag as if the title bar were pressed.</summary>
    internal static void BeginDrag(IntPtr window)
    {
        ReleaseCapture();
        SendMessage(window, WmNcLButtonDown, HtCaption, IntPtr.Zero);
    }

    /// <summary>Windows 11 rounded corners; earlier Windows refuses the attribute and keeps square ones.</summary>
    internal static void RoundCorners(IntPtr window)
    {
        int preference = DwmCornerRound;
        _ = DwmSetWindowAttribute(window, DwmWindowCornerPreference, ref preference, sizeof(int));
    }
}
