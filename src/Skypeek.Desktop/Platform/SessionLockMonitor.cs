using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Skypeek.Desktop.Platform;

/// <summary>
/// Raises <see cref="Locked"/> when the OS session locks: Windows session switch, macOS screen lock (polled), Linux
/// logind Lock / LockedHint (via gdbus monitor). Raised on a background thread.
/// </summary>
public sealed class SessionLockMonitor : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Process? _monitor;

    public event Action? Locked;

    public void Start()
    {
        if (OperatingSystem.IsWindows())
            StartWindows();
        else if (OperatingSystem.IsMacOS())
            _ = PollMacAsync(_cts.Token);
        else if (OperatingSystem.IsLinux())
            StartLinux();
    }

    [SupportedOSPlatform("windows")]
    private void StartWindows()
    {
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch
        {
            // No session events (e.g. service session).
        }
    }

    [SupportedOSPlatform("windows")]
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            Locked?.Invoke();
    }

    // ---------------- macOS ----------------

    private async Task PollMacAsync(CancellationToken ct)
    {
        var wasLocked = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            bool locked;
            try
            {
                locked = MacScreenIsLocked();
            }
            catch
            {
                return; // API unavailable: stop polling
            }
            if (locked && !wasLocked)
                Locked?.Invoke();
            wasLocked = locked;
        }
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreGraphics)] private static extern IntPtr CGSessionCopyCurrentDictionary();
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [DllImport(CoreFoundation)] private static extern bool CFBooleanGetValue(IntPtr boolean);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);

    /// <summary>The login window's "CGSSessionScreenIsLocked" flag of the current session.</summary>
    private static bool MacScreenIsLocked()
    {
        var session = CGSessionCopyCurrentDictionary();
        if (session == IntPtr.Zero)
            return false;
        var key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked", 0x08000100 /* UTF-8 */);
        try
        {
            var value = CFDictionaryGetValue(session, key);
            return value != IntPtr.Zero && CFBooleanGetValue(value);
        }
        finally
        {
            CFRelease(key);
            CFRelease(session);
        }
    }

    // ---------------- Linux ----------------

    /// <summary>Watches logind on the system bus: GNOME, KDE and most desktops report locking there.</summary>
    private void StartLinux()
    {
        try
        {
            var start = new ProcessStartInfo("gdbus") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "monitor", "--system", "--dest", "org.freedesktop.login1" })
                start.ArgumentList.Add(a);
            _monitor = Process.Start(start);
        }
        catch
        {
            return; // gdbus missing: no lock detection (idle lockout still works)
        }
        if (_monitor is null)
            return;
        _ = _monitor.StandardError.ReadToEndAsync();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested && await _monitor.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Contains("org.freedesktop.login1.Session.Lock", StringComparison.Ordinal)
                    || line.Contains("'LockedHint': <true>", StringComparison.Ordinal))
                    Locked?.Invoke();
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
            }
            catch
            {
                // ignore
            }
        }
        try
        {
            if (_monitor is { HasExited: false })
                _monitor.Kill();
        }
        catch
        {
            // already gone
        }
        _monitor?.Dispose();
    }
}
