using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Storage;
using Velopack;
using Velopack.Logging;

namespace Skypeek.Desktop.Updates;

/// <summary>How this copy was installed, which decides what an update can do.</summary>
public enum InstallKind
{
    /// <summary>Through the installer (or the portable zip / AppImage): updates download and install themselves.</summary>
    Installed,
    /// <summary>The plain single-file download: a new version is only announced, with a link to the download page.</summary>
    SingleFile,
    /// <summary>Run from a build folder (dotnet run): checks only on request.</summary>
    Development,
}

public enum UpdateStage { Disabled, Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>
/// Checks the signed update feed a few minutes after start and then every few hours (unless turned off in
/// Settings → General). Installed copies download the update in the background and apply it when the user restarts
/// Skypeek from the tray or Settings, or at the next start; single-file copies only say that a new version exists.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly SignedUpdateSource? _source;
    private readonly UpdateManager? _manager;
    private readonly Func<bool> _autoCheckEnabled;
    private readonly Action<string, string> _notify;
    private readonly DispatcherTimer _timer;
    private UpdateInfo? _pending;
    private string? _announced;
    private bool _busy;

    public InstallKind Kind { get; }
    public UpdateStage Stage { get; private set; } = UpdateStage.Idle;
    /// <summary>The newer version found (Available, Downloading, Ready).</summary>
    public string? NewVersion { get; private set; }
    public int DownloadPercent { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LastCheckedUtc { get; private set; }
    public static string? DownloadPage => SignedUpdateSource.BuildSetting("SkypeekDownloadPage");

    /// <summary>Raised on the UI thread whenever the state changes.</summary>
    public event Action? Changed;

    public UpdateService(Func<bool> autoCheckEnabled, Action<string, string> notify)
    {
        _autoCheckEnabled = autoCheckEnabled;
        _notify = notify;
        _source = SignedUpdateSource.FromBuild();
        if (_source is not null)
        {
            try
            {
                var manager = new UpdateManager(_source);
                if (manager.IsInstalled)
                    _manager = manager;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                App.DebugLog(ex);
            }
        }
        // Not installed: the published single file, or a Debug build run from the source tree.
#if DEBUG
        Kind = _manager is not null ? InstallKind.Installed : InstallKind.Development;
#else
        Kind = _manager is not null ? InstallKind.Installed : InstallKind.SingleFile;
#endif
        if (_source is null || !_source.HasKeys)
        {
            Stage = UpdateStage.Disabled;
            Error = _source is null ? "this build has no update address" : "this build has no update signing key";
        }
        // A downloaded update waiting for a restart (e.g. the user exited before restarting).
        if (_manager?.UpdatePendingRestart is { } waiting)
        {
            Stage = UpdateStage.Ready;
            NewVersion = waiting.Version.ToString();
        }

        _timer = new DispatcherTimer { Interval = FirstCheck };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Interval;
            if (_autoCheckEnabled() && Kind != InstallKind.Development)
                await CheckAsync();
        };
        if (Stage != UpdateStage.Disabled)
            _timer.Start();
    }

    public string Describe() => Kind switch
    {
        InstallKind.Installed => "installed: updates download and install themselves",
        InstallKind.SingleFile => "single file: new versions are announced here, download them from the site",
        _ => "development build",
    };

    /// <summary>Checks now (Settings → Check for updates, or the timer); on an installed copy also downloads.</summary>
    public async Task CheckAsync()
    {
        if (_busy || _source is null || Stage is UpdateStage.Disabled or UpdateStage.Ready)
            return;
        _busy = true;
        Set(UpdateStage.Checking);
        try
        {
            if (_manager is not null)
                await CheckInstalledAsync();
            else
                await CheckAnnouncedAsync();
        }
        catch (Exception ex) when (ex is UpdateCheckException or HttpRequestException or TaskCanceledException or IOException
                                       or Velopack.Exceptions.ChecksumFailedException or Velopack.Exceptions.AcquireLockFailedException)
        {
            App.DebugLog(ex);
            Error = ex is TaskCanceledException ? "the update server did not answer" : ex.Message;
            Set(UpdateStage.Failed);
        }
        finally
        {
            LastCheckedUtc = DateTime.UtcNow;
            _busy = false;
            Changed?.Invoke();
        }
    }

    private async Task CheckInstalledAsync()
    {
        var info = await _manager!.CheckForUpdatesAsync();
        if (info is null)
        {
            Set(UpdateStage.UpToDate);
            return;
        }
        NewVersion = info.TargetFullRelease.Version.ToString();
        DownloadPercent = 0;
        Set(UpdateStage.Downloading);
        await _manager.DownloadUpdatesAsync(info, p => Dispatcher.UIThread.Post(() =>
        {
            DownloadPercent = p;
            Changed?.Invoke();
        }));
        _pending = info;
        Set(UpdateStage.Ready);
        Announce($"{AppInfo.Name} {NewVersion} is ready",
            "Restart Skypeek to install it (tray menu or Settings → General); otherwise it installs at the next start.");
    }

    private async Task CheckAnnouncedAsync()
    {
        // Release channels are named like runtime ids (tools/publish.ps1), e.g. win-x64.
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var channel = $"{os}-{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
        var feed = await _source!.GetReleaseFeed(NullVelopackLogger.Instance, null, channel);
        var latest = feed.Assets.Where(a => a.Type == VelopackAssetType.Full).MaxBy(a => a.Version);
        if (latest is null || !SemanticVersion.TryParse(AppInfo.Version, out var current) || latest.Version.CompareTo(current) <= 0)
        {
            Set(UpdateStage.UpToDate);
            return;
        }
        NewVersion = latest.Version.ToString();
        Set(UpdateStage.Available);
        Announce($"{AppInfo.Name} {NewVersion} is available", "Download it from the Skypeek site (Settings → General has the link).");
    }

    /// <summary>Installs the downloaded update and starts the new version (tray menu or Settings).</summary>
    public void RestartToUpdate()
    {
        if (_manager is null || Stage != UpdateStage.Ready)
            return;
        var asset = _pending?.TargetFullRelease ?? _manager.UpdatePendingRestart;
        if (asset is null)
            return;
        // Velopack's updater waits for this process to exit, swaps the files and starts the new version.
        _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: true);
        App.Current.ExitApp();
    }

    private void Announce(string title, string message)
    {
        if (_announced == NewVersion)
            return;
        _announced = NewVersion;
        _notify(title, message);
    }

    private void Set(UpdateStage stage)
    {
        Stage = stage;
        if (stage is not UpdateStage.Failed)
            Error = null;
        Changed?.Invoke();
    }

    public void Dispose() => _timer.Stop();

    /// <summary>The "check automatically" preference; kept outside the encrypted vault so it applies while locked.</summary>
    public static bool AutoCheckSetting(string vaultDirectory) => Vault.ReadMeta(vaultDirectory)?.CheckForUpdates ?? true;
}
