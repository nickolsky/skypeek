using Skypeek.Core;
using Skypeek.Desktop.Infrastructure;
#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
#endif

namespace Skypeek.Desktop.Platform;

/// <summary>
/// Desktop notifications: Windows toasts, macOS Notification Center (via osascript) and the freedesktop notification
/// service on Linux (notify-send, or gdbus). While the UI is locked only the title (target + resource) is shown.
/// </summary>
public sealed class DesktopNotifier(Func<bool> isLocked) : INotifier
{
#pragma warning disable CS0067 // raised only by Windows toasts
    /// <summary>Raised when the user clicks a notification (Windows only; elsewhere the click just dismisses it).</summary>
    public static event Action? Activated;
#pragma warning restore CS0067

    public static void Initialize()
    {
#if WINDOWS
        try
        {
            ToastNotificationManagerCompat.OnActivated += _ => Activated?.Invoke();
        }
        catch
        {
            // Notifications unavailable (policy or unpackaged restrictions).
        }
#endif
    }

    public void Notify(string title, string message, string? detail = null)
    {
        var body = isLocked() ? $"Details hidden while {AppInfo.Name} is locked." : detail is null ? message : $"{message}\n{detail}";
        // Never block the caller (health polling) on a helper process.
        _ = Task.Run(() => Show(title, body));
    }

    /// <summary>Shows a notification right away; returns why it could not be shown, or null.</summary>
    public static string? Test(string title, string body) => Show(title, body);

    /// <summary>GVariant text string literal for gdbus.</summary>
    private static string Quote(string text) => "'" + text.Replace(@"\", @"\\").Replace("'", @"\'").Replace("\n", @"\n") + "'";

    private static string? Show(string title, string body)
    {
        try
        {
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                var builder = new ToastContentBuilder().AddArgument("action", "dashboard").AddText(title);
                foreach (var line in body.Split('\n', 2))
                    builder.AddText(line);
                builder.Show();
                return null;
            }
#endif
            if (OperatingSystem.IsMacOS())
            {
                // The text is passed as script arguments, never spliced into the script.
                PlatformInfo.TryRun("osascript", ["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run",
                    title, body]);
                return null;
            }
            if (OperatingSystem.IsLinux())
            {
                if (!PlatformInfo.TryRun("notify-send", ["--app-name", AppInfo.Name, title, body])
                    && !PlatformInfo.TryRun("gdbus", ["call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications",
                        "--method", "org.freedesktop.Notifications.Notify", Quote(AppInfo.Name), "uint32 0", "''", Quote(title), Quote(body), "@as []", "@a{sv} {}", "int32 10000"]))
                    return "no notification service found (notify-send or gdbus)";
            }
            return null;
        }
        catch (Exception ex)
        {
            // Notifications can be disabled by the user or policy.
            return ex.Message;
        }
    }
}
