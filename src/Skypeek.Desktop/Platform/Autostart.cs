using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Skypeek.Desktop.Platform;

/// <summary>
/// "Start with the computer": the per-user Run key on Windows, a LaunchAgent on macOS, an XDG autostart entry on
/// Linux. Started that way the app goes to the tray (--tray).
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Skypeek";
    private const string LegacyValueName = "AwsManager";
    private const string MacLabel = "net.skypeek.desktop";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string MacPlist => Path.Combine(Home, "Library", "LaunchAgents", MacLabel + ".plist");

    private static string LinuxDesktopFile =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Home, ".config"), "autostart", "skypeek.desktop");

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        return File.Exists(OperatingSystem.IsMacOS() ? MacPlist : LinuxDesktopFile);
    }

    /// <summary>Replaces the pre-1.0 "AwsManager" startup entry on Windows with one for this executable.</summary>
    public static void MigrateLegacy()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is null)
            return;
        key.DeleteValue(LegacyValueName);
        key.SetValue(ValueName, WindowsCommand());
    }

    public static void SetEnabled(bool enabled)
    {
        if (OperatingSystem.IsWindows())
            SetWindows(enabled);
        else if (OperatingSystem.IsMacOS())
            SetFile(MacPlist, enabled, MacPlistText);
        else
            SetFile(LinuxDesktopFile, enabled, DesktopEntryText);
    }

    /// <summary>After installing or updating: an existing startup entry now starts this copy (e.g. the installed one).</summary>
    public static void RefreshPath()
    {
        try
        {
            if (IsEnabled())
                SetEnabled(true);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Settings → General can set it again.
        }
    }

    /// <summary>Uninstall: removes the startup entry if it starts a program inside <paramref name="directory"/>.</summary>
    public static void RemoveIfPointsInto(string directory)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(directory, ".."));
            if (Command() is { } command && command.Contains(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                SetEnabled(false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Nothing else to do while uninstalling.
        }
    }

    /// <summary>What the startup entry runs (the Run value, or the file's text), or null.</summary>
    private static string? Command()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        var path = OperatingSystem.IsMacOS() ? MacPlist : LinuxDesktopFile;
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, WindowsCommand());
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }

    private static string WindowsCommand()
    {
        var (file, args) = PlatformInfo.SelfCommand("--tray");
        return string.Join(" ", new[] { file }.Concat(args).Select(a => a.StartsWith('-') ? a : $"\"{a}\""));
    }

    private static void SetFile(string path, bool enabled, Func<string> content)
    {
        if (!enabled)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content());
    }

    private static string MacPlistText()
    {
        var (file, args) = PlatformInfo.SelfCommand("--tray");
        var items = string.Concat(new[] { file }.Concat(args).Select(a => $"\n        <string>{SecurityElement.Escape(a)}</string>"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{MacLabel}</string>
                <key>ProgramArguments</key>
                <array>{items}
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>ProcessType</key>
                <string>Interactive</string>
            </dict>
            </plist>

            """;
    }

    /// <summary>XDG desktop entry; Exec arguments are quoted per the spec.</summary>
    private static string DesktopEntryText()
    {
        var (file, args) = PlatformInfo.SelfCommand("--tray");
        static string Quote(string a) => a.StartsWith('-') ? a : "\"" + new StringBuilder(a).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
        return $"""
            [Desktop Entry]
            Type=Application
            Name=Skypeek
            Comment=AWS status in the tray
            Exec={string.Join(" ", new[] { file }.Concat(args).Select(Quote))}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """;
    }
}
