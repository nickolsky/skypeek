using System.Runtime.InteropServices;

namespace Skypeek.Desktop.Platform;

/// <summary>
/// Time since the last keyboard/mouse input: session-wide on Windows, macOS and X11; on Wayland (no such API) the
/// time since the last input to Skypeek's own windows, which locks sooner but never later.
/// </summary>
public static class IdleTime
{
    private static long _lastAppInputTicks = Environment.TickCount64;
    private static bool _x11Failed;
    private static IntPtr _display;
    private static IntPtr _info;

    /// <summary>Called for every input event in the app's windows.</summary>
    public static void NoteAppInput() => Interlocked.Exchange(ref _lastAppInputTicks, Environment.TickCount64);

    private static TimeSpan SinceAppInput => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastAppInputTicks));

    public static TimeSpan Get()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Windows();
            if (OperatingSystem.IsMacOS())
                return TimeSpan.FromSeconds(CGEventSourceSecondsSinceLastEventType(1 /* HID system state */, uint.MaxValue /* any input */));
            if (OperatingSystem.IsLinux() && !PlatformInfo.IsWayland && X11() is { } x11)
                return x11;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // fall back to the app's own input
        }
        return SinceAppInput;
    }

    // ---------------- Windows ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private static TimeSpan Windows()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    // ---------------- macOS ----------------

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern double CGEventSourceSecondsSinceLastEventType(int sourceState, uint eventType);

    // ---------------- X11 (XScreenSaver extension) ----------------

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libXss.so.1")] private static extern IntPtr XScreenSaverAllocInfo();
    [DllImport("libXss.so.1")] private static extern int XScreenSaverQueryInfo(IntPtr display, IntPtr drawable, IntPtr info);

    private static TimeSpan? X11()
    {
        if (_x11Failed)
            return null;
        try
        {
            if (_display == IntPtr.Zero)
            {
                _display = XOpenDisplay(IntPtr.Zero);
                _info = _display == IntPtr.Zero ? IntPtr.Zero : XScreenSaverAllocInfo();
                if (_display == IntPtr.Zero || _info == IntPtr.Zero)
                {
                    _x11Failed = true;
                    return null;
                }
            }
            if (XScreenSaverQueryInfo(_display, XDefaultRootWindow(_display), _info) == 0)
                return null;
            // XScreenSaverInfo: Window window; int state; int kind; unsigned long til_or_since; unsigned long idle (ms); …
            var idle = Marshal.ReadInt64(_info, IntPtr.Size + 8 + IntPtr.Size);
            return TimeSpan.FromMilliseconds(idle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _x11Failed = true;
            return null;
        }
    }
}
