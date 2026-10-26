using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CodexCompanion.Core;
namespace CodexCompanion.App;

internal static class NativeWindows
{
    static uint cachedPid;
    static bool cachedMatch;
    static long cachedUntil;
    internal const int GwlExStyle = -20, WsExToolwindow = 0x80, WsExNoactivate = 0x08000000;
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; public readonly PixelRect Pixels => new(Left, Top, Right - Left, Bottom - Top); }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint h, uint flags);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint h, out RECT rect);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint h, out RECT rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint h, ref POINT point);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint h);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] internal static extern int GetWindowLong(nint h, int index);
    [DllImport("user32.dll")] internal static extern int SetWindowLong(nint h, int index, int value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint h, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] internal static extern bool AttachConsole(uint process);
    internal static PixelRect WorkArea(nint h)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(MonitorFromWindow(h, 2), ref info) ? info.Work.Pixels : new(0, 0, 1920, 1080);
    }
    internal static bool IsCodex(nint h)
    {
        if (h == 0) return false;
        GetWindowThreadProcessId(h, out var pid);
        if (pid == cachedPid && Environment.TickCount64 < cachedUntil) return cachedMatch;
        cachedPid = pid; cachedUntil = Environment.TickCount64 + 5000; cachedMatch = false;
        var process = OpenProcess(0x1000, false, pid);
        if (process == 0) return false;
        try
        {
            var name = new StringBuilder(1024); var length = name.Capacity;
            return cachedMatch = QueryFullProcessImageName(process, 0, name, ref length) && HostIdentity.IsCodexPath(name.ToString());
        }
        finally { CloseHandle(process); }
    }
    internal static (nint Handle, PixelRect Rect, PixelRect Work, double Scale)? ForegroundCodex()
    {
        var h = GetAncestor(GetForegroundWindow(), 2);
        if (!IsCodex(h) || IsIconic(h) || !IsWindowVisible(h)) return null;
        if (DwmGetWindowAttribute(h, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        if (!GetClientRect(h, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return null;
        var origin = new POINT(); if (!ClientToScreen(h, ref origin)) return null;
        rect.Right += origin.X; rect.Bottom += origin.Y; rect.Left += origin.X; rect.Top += origin.Y;
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(h, 2), ref info);
        var dpi = GetDpiForWindow(h);
        return (h, rect.Pixels, info.Work.Pixels, dpi > 0 ? dpi / 96d : 1d);
    }
}
