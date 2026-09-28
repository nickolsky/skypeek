using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Skypeek.App.Infrastructure;

/// <summary>
/// One instance per user and vault. A second launch asks the running one to show itself. A separate vault
/// (SKYPEEK_HOME) gets its own instance so a test vault can run next to the real one.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Name = BuildName();

    private static string BuildName()
    {
        var name = $"Skypeek.{Environment.UserName}";
        if (Environment.GetEnvironmentVariable("SKYPEEK_HOME") is { Length: > 0 } home)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(home).ToLowerInvariant()));
            name += "." + Convert.ToHexString(hash)[..12];
        }
        return name;
    }
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, $@"Local\{Name}", out var created);
        if (created)
            return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public static void SignalExisting(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.Write(message);
        }
        catch
        {
            // The running instance may be shutting down.
        }
    }

    public void Listen(Action<string> onMessage)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server);
                    onMessage(await reader.ReadToEndAsync(_cts.Token));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

/// <summary>System-wide hotkey via RegisterHotKey on a message-only window.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xA115;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private bool _registered;

    public GlobalHotkey()
    {
        _source = new HwndSource(new HwndSourceParameters("SkypeekHotkey") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    public event Action? Pressed;

    public string? Current { get; private set; }

    /// <summary>Registers the hotkey (e.g. "Win+Alt+A"). Returns false if it is invalid or taken by another app.</summary>
    public bool TryRegister(string hotkey)
    {
        if (!TryParse(hotkey, out var mods, out var vk))
            return false;

        Unregister();
        if (!RegisterHotKey(_source.Handle, HotkeyId, mods | ModNoRepeat, vk))
            return false;
        _registered = true;
        Current = hotkey;
        return true;
    }

    /// <summary>Checks whether a hotkey is free without keeping it (the current one counts as free).</summary>
    public bool IsAvailable(string hotkey)
    {
        if (string.Equals(hotkey, Current, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!TryParse(hotkey, out var mods, out var vk))
            return false;
        const int probeId = HotkeyId + 1;
        if (!RegisterHotKey(_source.Handle, probeId, mods | ModNoRepeat, vk))
            return false;
        UnregisterHotKey(_source.Handle, probeId);
        return true;
    }

    public static bool TryParse(string hotkey, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "windows": modifiers |= ModWin; break;
                default: return false;
            }
        }

        if (!Enum.TryParse<Key>(parts[^1], ignoreCase: true, out var key))
            return false;
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return modifiers != 0 && virtualKey != 0;
    }

    public static string? Format(ModifierKeys modifiers, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (parts.Count == 0)
            return null;
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void Unregister()
    {
        if (_registered)
            UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        Current = null;
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Skypeek";
    private const string LegacyValueName = "AwsManager";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Replaces the pre-1.0 "AwsManager" startup entry with one for this executable.</summary>
    public static void MigrateLegacy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is null)
            return;
        key.DeleteValue(LegacyValueName);
        key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
    }
}

public static class IdleTime
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Time since the last keyboard/mouse input anywhere in the session.</summary>
    public static TimeSpan Get()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }
}

/// <summary>
/// Copies sensitive values while opting out of Windows clipboard history and cloud sync, and clears the clipboard
/// after a timeout if it still holds the value.
/// </summary>
public static class SecureClipboard
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);
    private static string? _lastSecret;
    private static CancellationTokenSource? _clearCts;

    public static void CopySecret(string value)
    {
        var data = new DataObject();
        data.SetText(value, TextDataFormat.UnicodeText);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(BitConverter.GetBytes(1)));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        Retry(() => Clipboard.SetDataObject(data, copy: true));

        _lastSecret = value;
        _clearCts?.Cancel();
        var cts = _clearCts = new CancellationTokenSource();
        _ = Task.Delay(ClearAfter, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Application.Current?.Dispatcher.Invoke(ClearIfOwned);
        }, TaskScheduler.Default);
    }

    public static void CopyPlain(string value) => Retry(() => Clipboard.SetText(value));

    public static void ClearIfOwned()
    {
        if (_lastSecret is null)
            return;
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == _lastSecret)
                Clipboard.Clear();
        }
        catch (COMException)
        {
            // Clipboard busy; best effort.
        }
        _lastSecret = null;
    }

    private static void Retry(Action action)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (COMException) when (i < 5)
            {
                Thread.Sleep(50);
            }
        }
    }
}
