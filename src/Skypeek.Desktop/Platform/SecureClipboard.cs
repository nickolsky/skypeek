using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Skypeek.Desktop.Platform;

/// <summary>
/// Copies sensitive values with the platform's "do not record" hints (Windows clipboard history and cloud sync, the
/// macOS concealed type used by password managers, KDE Klipper's password hint) and clears the clipboard after a
/// timeout if it still holds the value.
/// </summary>
public static class SecureClipboard
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);
    private static string? _lastSecret;
    private static CancellationTokenSource? _clearCts;

    private static IClipboard? Clipboard =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive)?.Clipboard ?? desktop.Windows.FirstOrDefault()?.Clipboard
            : null;

    public static async Task CopySecretAsync(string value)
    {
        if (Clipboard is not { } clipboard)
            return;
        var item = new DataTransferItem();
        item.Set(DataFormat.Text, value);
        if (OperatingSystem.IsWindows())
        {
            item.Set(DataFormat.CreateBytesPlatformFormat("ExcludeClipboardContentFromMonitorProcessing"), BitConverter.GetBytes(1));
            item.Set(DataFormat.CreateBytesPlatformFormat("CanIncludeInClipboardHistory"), BitConverter.GetBytes(0));
            item.Set(DataFormat.CreateBytesPlatformFormat("CanUploadToCloudClipboard"), BitConverter.GetBytes(0));
        }
        else if (OperatingSystem.IsMacOS())
        {
            item.Set(DataFormat.CreateBytesPlatformFormat("org.nspasteboard.ConcealedType"), []);
        }
        else
        {
            item.Set(DataFormat.CreateBytesPlatformFormat("x-kde-passwordManagerHint"), Encoding.UTF8.GetBytes("secret"));
        }
        var data = new DataTransfer();
        data.Add(item);
        await clipboard.SetDataAsync(data);

        _lastSecret = value;
        _clearCts?.Cancel();
        var cts = _clearCts = new CancellationTokenSource();
        _ = Task.Delay(ClearAfter, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                Dispatcher.UIThread.Post(() => _ = ClearIfOwnedAsync());
        }, TaskScheduler.Default);
    }

    public static async Task CopyPlainAsync(string value)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(value);
    }

    /// <summary>Fire-and-forget copy for click handlers.</summary>
    public static void CopyPlain(string value) => _ = CopyPlainAsync(value);

    public static async Task ClearIfOwnedAsync()
    {
        if (_lastSecret is null)
            return;
        var secret = _lastSecret;
        _lastSecret = null;
        try
        {
            if (Clipboard is { } clipboard && await clipboard.TryGetTextAsync() == secret)
                await clipboard.ClearAsync();
        }
        catch
        {
            // Clipboard busy; best effort.
        }
    }
}
