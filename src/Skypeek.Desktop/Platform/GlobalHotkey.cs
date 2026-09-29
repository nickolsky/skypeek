using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Threading;

namespace Skypeek.Desktop.Platform;

/// <summary>
/// System-wide hotkey ("Win+Alt+A"; Win is the Windows/Super/Command key). Windows: RegisterHotKey on a dedicated
/// message thread. Linux X11: XGrabKey on the root window (not available on Wayland). macOS: not supported yet.
/// </summary>
public abstract class GlobalHotkey : IDisposable
{
    public static GlobalHotkey Create() =>
        OperatingSystem.IsWindows() ? new WindowsHotkey()
        : OperatingSystem.IsLinux() && !PlatformInfo.IsWayland && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } ? new X11Hotkey()
        : new UnsupportedHotkey();

    /// <summary>Raised on the UI thread.</summary>
    public event Action? Pressed;

    public string? Current { get; protected set; }

    public virtual bool IsSupported => true;

    /// <summary>Why the hotkey is not available here (settings text), or null.</summary>
    public virtual string? UnsupportedReason => null;

    /// <summary>Registers the hotkey. Returns false if it is invalid or taken by another app.</summary>
    public abstract bool TryRegister(string hotkey);

    /// <summary>Checks whether a hotkey is free without keeping it (the current one counts as free).</summary>
    public abstract bool IsAvailable(string hotkey);

    public abstract void Dispose();

    protected void RaisePressed() => Dispatcher.UIThread.Post(() => Pressed?.Invoke());

    /// <summary>"Win+Alt+A" → modifier names (lowercase) and the key name ("A", "F5", "D1", "Space"…).</summary>
    public static bool TryParse(string hotkey, out HashSet<string> modifiers, out string key)
    {
        modifiers = new HashSet<string>();
        key = "";
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;
        foreach (var part in parts[..^1])
        {
            var m = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => "ctrl",
                "alt" or "option" => "alt",
                "shift" => "shift",
                "win" or "windows" or "super" or "cmd" or "command" or "meta" => "win",
                _ => null,
            };
            if (m is null)
                return false;
            modifiers.Add(m);
        }
        key = parts[^1];
        return modifiers.Count > 0 && Enum.TryParse<Key>(key, ignoreCase: true, out _);
    }

    /// <summary>The hotkey text for a key press in the settings box, or null for a modifier-only press.</summary>
    public static string? Format(KeyModifiers modifiers, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None)
            return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (parts.Count == 0)
            return null;
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}

internal sealed class UnsupportedHotkey : GlobalHotkey
{
    public override bool IsSupported => false;

    public override string? UnsupportedReason => OperatingSystem.IsMacOS()
        ? "A global hotkey is not available on macOS yet; use the menu bar icon."
        : "A global hotkey is not available on Wayland; use the tray icon, or bind a desktop shortcut to start Skypeek (a second start shows the window).";

    public override bool TryRegister(string hotkey)
    {
        Current = hotkey;
        return false;
    }

    public override bool IsAvailable(string hotkey) => true;

    public override void Dispose()
    {
    }
}

/// <summary>Runs every call to the native API on one dedicated thread (Win32 hotkeys belong to a thread; Xlib is not thread-safe).</summary>
internal abstract class HotkeyThread : GlobalHotkey
{
    private readonly BlockingCollection<(Func<bool> Work, TaskCompletionSource<bool> Done)> _work = new();
    private readonly Thread _thread;
    protected volatile bool Stopping;

    protected HotkeyThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    protected abstract void Run();

    /// <summary>Wakes the thread so it drains the work queue.</summary>
    protected abstract void Wake();

    protected void DrainWork()
    {
        while (_work.TryTake(out var item))
        {
            try
            {
                item.Done.TrySetResult(item.Work());
            }
            catch (Exception ex)
            {
                item.Done.TrySetException(ex);
            }
        }
    }

    protected bool Invoke(Func<bool> work)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add((work, done));
        Wake();
        return done.Task.Wait(TimeSpan.FromSeconds(3)) && done.Task.Result;
    }

    public override void Dispose()
    {
        Stopping = true;
        Wake();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}

internal sealed class WindowsHotkey : HotkeyThread
{
    private const int WmHotkey = 0x0312, WmApp = 0x8000, WmQuit = 0x0012;
    private const int HotkeyId = 0xA115;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private bool _registered;

    public WindowsHotkey() : base("Skypeek hotkey") => _ready.Wait(TimeSpan.FromSeconds(3));

    protected override void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // creates the thread's message queue
        _ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmHotkey && (int)msg.wParam == HotkeyId)
                RaisePressed();
            else if (msg.message == WmApp)
                DrainWork();
            if (Stopping)
                break;
        }
        if (_registered)
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
    }

    protected override void Wake() => PostThreadMessage(_threadId, Stopping ? WmQuit : WmApp, UIntPtr.Zero, IntPtr.Zero);

    private static bool ToNative(string hotkey, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (!TryParse(hotkey, out var modifiers, out var key) || !Enum.TryParse<Key>(key, true, out var parsed))
            return false;
        if (modifiers.Contains("ctrl")) mods |= ModControl;
        if (modifiers.Contains("alt")) mods |= ModAlt;
        if (modifiers.Contains("shift")) mods |= ModShift;
        if (modifiers.Contains("win")) mods |= ModWin;
        vk = VirtualKey(parsed);
        return vk != 0;
    }

    /// <summary>Avalonia key → Win32 virtual-key code for the keys a hotkey can use.</summary>
    private static uint VirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (uint)(0x41 + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (uint)(0x30 + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (uint)(0x60 + (key - Key.NumPad0)),
        >= Key.F1 and <= Key.F24 => (uint)(0x70 + (key - Key.F1)),
        Key.Space => 0x20,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.End => 0x23,
        Key.Home => 0x24,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.Insert => 0x2D,
        Key.Delete => 0x2E,
        Key.OemTilde => 0xC0,
        Key.OemMinus => 0xBD,
        Key.OemPlus => 0xBB,
        Key.OemComma => 0xBC,
        Key.OemPeriod => 0xBE,
        _ => 0,
    };

    public override bool TryRegister(string hotkey)
    {
        if (!ToNative(hotkey, out var mods, out var vk))
            return false;
        var ok = Invoke(() =>
        {
            if (_registered)
                UnregisterHotKey(IntPtr.Zero, HotkeyId);
            _registered = RegisterHotKey(IntPtr.Zero, HotkeyId, mods | ModNoRepeat, vk);
            return _registered;
        });
        Current = ok ? hotkey : null;
        return ok;
    }

    public override bool IsAvailable(string hotkey)
    {
        if (string.Equals(hotkey, Current, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!ToNative(hotkey, out var mods, out var vk))
            return false;
        return Invoke(() =>
        {
            const int probeId = HotkeyId + 1;
            if (!RegisterHotKey(IntPtr.Zero, probeId, mods | ModNoRepeat, vk))
                return false;
            UnregisterHotKey(IntPtr.Zero, probeId);
            return true;
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, int msg, UIntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

/// <summary>X11: grabs the key on the root window of a private display connection (works on X11 sessions, not Wayland).</summary>
internal sealed class X11Hotkey : HotkeyThread
{
    private const int KeyPress = 2, GrabModeAsync = 1;
    private const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8 /* Alt */, Mod2Mask = 16 /* NumLock */, Mod4Mask = 64 /* Super */;
    private IntPtr _display;
    private IntPtr _root;
    private (int Code, uint Mods)? _grabbed;
    private readonly IntPtr _event = Marshal.AllocHGlobal(256);

    public X11Hotkey() : base("Skypeek hotkey (X11)")
    {
    }

    protected override void Run()
    {
        try
        {
            _display = XOpenDisplay(IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _display = IntPtr.Zero;
        }
        if (_display != IntPtr.Zero)
            _root = XDefaultRootWindow(_display);
        while (!Stopping)
        {
            DrainWork();
            if (_display != IntPtr.Zero)
            {
                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, _event);
                    if (Marshal.ReadInt32(_event) == KeyPress)
                        RaisePressed();
                }
            }
            Thread.Sleep(40);
        }
        if (_display != IntPtr.Zero)
        {
            Ungrab();
            XCloseDisplay(_display);
        }
        Marshal.FreeHGlobal(_event);
    }

    protected override void Wake()
    {
        // The loop polls; nothing to signal.
    }

    private bool ToNative(string hotkey, out int code, out uint mods)
    {
        code = 0;
        mods = 0;
        if (_display == IntPtr.Zero || !TryParse(hotkey, out var modifiers, out var key) || !Enum.TryParse<Key>(key, true, out var parsed))
            return false;
        if (modifiers.Contains("ctrl")) mods |= ControlMask;
        if (modifiers.Contains("alt")) mods |= Mod1Mask;
        if (modifiers.Contains("shift")) mods |= ShiftMask;
        if (modifiers.Contains("win")) mods |= Mod4Mask;
        var name = parsed switch
        {
            >= Key.A and <= Key.Z => parsed.ToString().ToLowerInvariant(),
            >= Key.D0 and <= Key.D9 => ((int)(parsed - Key.D0)).ToString(),
            >= Key.F1 and <= Key.F24 => parsed.ToString(),
            Key.Space => "space",
            Key.Home => "Home",
            Key.End => "End",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            _ => null,
        };
        if (name is null)
            return false;
        var keysym = XStringToKeysym(name);
        code = keysym == IntPtr.Zero ? 0 : XKeysymToKeycode(_display, keysym);
        return code != 0;
    }

    private static readonly uint[] IgnoredMasks = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];

    private void Ungrab()
    {
        if (_grabbed is not { } g)
            return;
        foreach (var extra in IgnoredMasks)
            XUngrabKey(_display, g.Code, g.Mods | extra, _root);
        XFlush(_display);
        _grabbed = null;
    }

    public override bool TryRegister(string hotkey)
    {
        var ok = Invoke(() =>
        {
            if (!ToNative(hotkey, out var code, out var mods))
                return false;
            Ungrab();
            // Also grab with Caps Lock / Num Lock on, which X11 counts as modifiers.
            foreach (var extra in IgnoredMasks)
                XGrabKey(_display, code, mods | extra, _root, false, GrabModeAsync, GrabModeAsync);
            XSync(_display, false);
            _grabbed = (code, mods);
            return true;
        });
        Current = ok ? hotkey : null;
        return ok;
    }

    public override bool IsAvailable(string hotkey) => Invoke(() => ToNative(hotkey, out _, out _));

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XStringToKeysym(string name);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport("libX11.so.6")] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window, bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport("libX11.so.6")] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window);
    [DllImport("libX11.so.6")] private static extern int XPending(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, bool discard);
}
