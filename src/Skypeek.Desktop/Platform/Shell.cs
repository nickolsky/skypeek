using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Skypeek.Desktop.Platform;

/// <summary>Opening links in the browser and running the AWS CLI sign-in.</summary>
public static class Shell
{
    /// <summary>Opens an https link (AWS console, Cost Explorer…) in the default browser.</summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        _ = OpenAsync(uri);
    }

    private static async Task OpenAsync(Uri uri)
    {
        var top = Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.Windows.FirstOrDefault()
            : null;
        try
        {
            if (top?.Launcher is { } launcher && await launcher.LaunchUriAsync(uri))
                return;
        }
        catch
        {
            // fall back below
        }
        var opener = OperatingSystem.IsWindows() ? null : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        try
        {
            if (opener is null)
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            else
                PlatformInfo.TryRun(opener, [uri.AbsoluteUri]);
        }
        catch
        {
            // no browser
        }
    }

    /// <summary>
    /// Runs <c>aws sso login --profile …</c>. On Windows in its own console window; elsewhere without a terminal, the
    /// CLI opens the browser itself and the first lines of its output (the sign-in URL and code) are reported.
    /// Returns an error message, or null once started.
    /// </summary>
    public static string? StartSsoLogin(string profile, Action<string>? output = null)
    {
        try
        {
            var start = new ProcessStartInfo("aws") { UseShellExecute = false };
            foreach (var a in new[] { "sso", "login", "--profile", profile })
                start.ArgumentList.Add(a);
            if (OperatingSystem.IsWindows())
            {
                start.CreateNoWindow = false; // a console program started from this GUI app gets its own console window
                Process.Start(start);
                return null;
            }
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            var process = Process.Start(start);
            if (process is null)
                return "Could not start the AWS CLI.";
            void Read(StreamReader reader) => _ = Task.Run(async () =>
            {
                while (await reader.ReadLineAsync() is { } line)
                    if (line.Trim().Length > 0)
                        output?.Invoke(line.Trim());
            });
            Read(process.StandardOutput);
            Read(process.StandardError);
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "The AWS CLI (aws) was not found. Install AWS CLI v2, or run the command yourself.";
        }
    }
}
