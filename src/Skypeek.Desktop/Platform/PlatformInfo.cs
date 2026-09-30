using System.Diagnostics;

namespace Skypeek.Desktop.Platform;

/// <summary>Which desktop the app runs on, and small helpers shared by the platform services.</summary>
public static class PlatformInfo
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>A Wayland session: X11-only features (global hotkey, system idle time) are not reliable there.</summary>
    public static bool IsWayland => IsLinux && (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland"
                                                || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 });

    /// <summary>
    /// Whether a tray / menu bar icon can be shown. Linux needs a session D-Bus with a status notifier host (KDE,
    /// GNOME with the AppIndicator extension, most others); without a session bus (e.g. WSLg) there is no tray.
    /// </summary>
    public static bool TrayAvailable => !IsLinux || Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is { Length: > 0 };

    /// <summary>"Windows lock", "screen lock" … as the user knows it (for settings text).</summary>
    public static string LockName => IsWindows ? "Windows locks (Win+L)" : IsMacOS ? "the Mac locks or the screen saver starts" : "the session locks";

    /// <summary>The command line that starts this app again (single-file executable, or dotnet + dll during development).</summary>
    public static (string File, string[] Arguments) SelfCommand(params string[] extra)
    {
        // An AppImage runs from a temporary mount; $APPIMAGE is the file itself.
        var process = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage && OperatingSystem.IsLinux()
            ? appImage
            : Environment.ProcessPath ?? "Skypeek";
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (process, [Path.Combine(AppContext.BaseDirectory, "Skypeek.dll"), .. extra]);
        return (process, extra);
    }

    /// <summary>Runs a helper program without a shell (arguments are passed as-is, never interpreted). Returns false if it is missing.</summary>
    public static bool TryRun(string file, IEnumerable<string> arguments, int timeoutMs = 3000)
    {
        try
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in arguments)
                start.ArgumentList.Add(a);
            using var process = Process.Start(start);
            if (process is null)
                return false;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { /* already gone */ }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
