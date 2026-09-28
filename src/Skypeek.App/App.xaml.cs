using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Skypeek.App.Infrastructure;
using Skypeek.App.Views;
using Skypeek.Aws;
using Skypeek.Core;
using Skypeek.Core.Logging;
using Skypeek.Core.Services;
using Skypeek.Storage;
using H.NotifyIcon;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace Skypeek.App;

public enum UiTarget
{
    None,
    Search,
    Dashboard,
    Problems,
    RequestLog,
    Settings,
}

public partial class App : Application
{
    private const string DefaultHotkey = "Win+Alt+A";
    private const int MaxFailedAttemptsBeforeDelay = 5;

    private SingleInstance? _instance;
    private GlobalHotkey? _hotkey;
    private TaskbarIcon? _tray;
    private MenuItem? _problemsItem;
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _statusDebounce;
    private UnlockWindow? _unlockWindow;
    private MainWindow? _main;
    private TrayStatus _status = TrayStatus.Empty;
    private int _failedAttempts;

    public static new App Current => (App)Application.Current;

    public RequestLogService Log { get; } = new();
    public AppSession? Session { get; private set; }
    public bool IsLocked { get; private set; } = true;
    public string VaultDirectory { get; private set; } = Vault.DefaultDirectory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            SingleInstance.SignalExisting("show");
            Shutdown();
            return;
        }
        _instance.Listen(_ => Dispatcher.BeginInvoke(() => RequestUi(UiTarget.Dashboard)));

        // One-time move from the old AwsManager name (vault folder and startup entry).
        VaultDirectory = Vault.ResolveDirectory();
        try
        {
            AutostartService.MigrateLegacy();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Startup entry stays as it was; Settings → General can set it again.
        }

        DispatcherUnhandledException += (_, args) =>
        {
            DebugLog(args.Exception);
            MessageBox.Show(args.Exception.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => DebugLog(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DebugLog(args.Exception);
            args.SetObserved();
        };

        ApplicationThemeManager.ApplySystemTheme();
        AwsPipeline.Install(Log);

        CreateTray();
        RegisterHotkey(Vault.ReadMeta(VaultDirectory)?.Hotkey ?? DefaultHotkey);

        ToastNotificationManagerCompat.OnActivated += _ => Dispatcher.BeginInvoke(() => RequestUi(UiTarget.Problems));
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();

        var trayOnly = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (!Vault.Exists(VaultDirectory) || !trayOnly)
            RequestUi(UiTarget.Dashboard);
        else
            new ToastNotifier(() => false).Notify($"{AppInfo.Name} is running",
                "Unlock it (click here, the tray icon or the hotkey) to start background refresh.");
    }

    // ---------------- tray ----------------

    private void CreateTray()
    {
        var menu = new ContextMenu();
        _problemsItem = Item("Problems (0)", () => RequestUi(UiTarget.Problems));
        menu.Items.Add(_problemsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Search…", () => RequestUi(UiTarget.Search)));
        menu.Items.Add(Item("Dashboard", () => RequestUi(UiTarget.Dashboard)));
        menu.Items.Add(Item("Request log", () => RequestUi(UiTarget.RequestLog)));
        menu.Items.Add(Item("Refresh all", () => { if (Session is not null) Session.Scheduler.RunNow(); else RequestUi(UiTarget.None); }));
        menu.Items.Add(Item("Settings", () => RequestUi(UiTarget.Settings)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Lock now", LockNow));
        menu.Items.Add(Item("Exit", ExitApp));

        _tray = new TaskbarIcon
        {
            ToolTipText = $"{AppInfo.Name} — locked",
            ContextMenu = menu,
            NoLeftClickDelay = true,
            Icon = IconRenderer.Create(false, 0, locked: true),
        };
        _tray.TrayLeftMouseUp += (_, _) => RequestUi(UiTarget.Dashboard);
        _tray.ForceCreate();

        static MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }

    private void ScheduleStatusRefresh()
    {
        if (_statusDebounce is null)
        {
            _statusDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _statusDebounce.Tick += (_, _) =>
            {
                _statusDebounce.Stop();
                RefreshTrayStatus();
            };
        }
        Dispatcher.BeginInvoke(() =>
        {
            _statusDebounce.Stop();
            _statusDebounce.Start();
        });
    }

    private void RefreshTrayStatus()
    {
        if (_tray is null)
            return;
        if (Session is null)
        {
            _tray.Icon = IconRenderer.Create(false, 0, locked: true);
            _tray.ToolTipText = $"{AppInfo.Name} — locked";
            return;
        }

        _status = Session.ComputeTrayStatus();
        var old = _tray.Icon;
        _tray.Icon = IconRenderer.Create(_status.IsRed, _status.Count);
        old?.Dispose();

        var tip = _status.Count == 0
            ? $"{AppInfo.Name} — all good"
            : $"{AppInfo.Name} — {_status.Count} problem(s)\n" + string.Join("\n", _status.Problems.Take(5).Select(p => Shorten(p.Display, 60)));
        _tray.ToolTipText = Shorten(tip, 127);
        if (_problemsItem is not null)
            _problemsItem.Header = $"Problems ({_status.Count})";
        _main?.OnStatusChanged(_status);
    }

    public TrayStatus Status => _status;

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // ---------------- hotkey ----------------

    public GlobalHotkey Hotkey
    {
        get
        {
            if (_hotkey is null)
            {
                _hotkey = new GlobalHotkey();
                _hotkey.Pressed += () => RequestUi(UiTarget.Search);
            }
            return _hotkey;
        }
    }

    public bool RegisterHotkey(string hotkey) => Hotkey.TryRegister(hotkey);

    // ---------------- unlock / lock ----------------

    /// <summary>Opens the requested UI, asking for the master password first if needed.</summary>
    public void RequestUi(UiTarget target)
    {
        if (Session is null || IsLocked)
        {
            ShowUnlock(target);
            return;
        }
        Open(target);
    }

    private void ShowUnlock(UiTarget then)
    {
        if (_unlockWindow is not null)
        {
            _unlockWindow.Then = then;
            _unlockWindow.Activate();
            return;
        }

        var mode = !Vault.Exists(VaultDirectory) ? UnlockMode.Create : Session is null ? UnlockMode.Unlock : UnlockMode.ReAuth;
        _unlockWindow = new UnlockWindow(mode, AttemptUnlockAsync) { Then = then };
        _unlockWindow.Closed += (_, _) => _unlockWindow = null;
        _unlockWindow.Show();
        _unlockWindow.Activate();
    }

    /// <summary>Returns an error message, or null on success.</summary>
    private async Task<string?> AttemptUnlockAsync(UnlockMode mode, string password)
    {
        if (_failedAttempts >= MaxFailedAttemptsBeforeDelay)
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * (_failedAttempts - MaxFailedAttemptsBeforeDelay + 1))));

        try
        {
            switch (mode)
            {
                case UnlockMode.Create:
                    var created = await Task.Run(() => Vault.Create(VaultDirectory, password, _hotkey?.Current ?? DefaultHotkey));
                    await StartSessionAsync(created);
                    break;
                case UnlockMode.Unlock:
                    var opened = await Task.Run(() => Vault.Open(VaultDirectory, password));
                    await StartSessionAsync(opened);
                    break;
                case UnlockMode.ReAuth:
                    var ok = await Task.Run(() => Session!.Vault.VerifyPassword(password));
                    if (!ok)
                        throw new InvalidPasswordException();
                    break;
            }
        }
        catch (InvalidPasswordException)
        {
            _failedAttempts++;
            return _failedAttempts >= MaxFailedAttemptsBeforeDelay
                ? $"Wrong password ({_failedAttempts} failed attempts — next try is delayed)."
                : "Wrong password.";
        }

        _failedAttempts = 0;
        IsLocked = false;
        RefreshTrayStatus();
        return null;
    }

    public void AfterUnlock(UiTarget then)
    {
        if (then != UiTarget.None)
            Open(then);
        if (Session is not null && Session.Settings.Targets.Count == 0)
            Open(UiTarget.Settings);
    }

    private async Task StartSessionAsync(Vault vault)
    {
        var session = await AppSession.StartAsync(vault, Log, new ToastNotifier(() => IsLocked), new ElevationApprover(this));
        Session = session;
        session.Health.Changed += ScheduleStatusRefresh;
        session.Monitor.StatusChanged += ScheduleStatusRefresh;
        session.Scheduler.JobCompleted += (_, _, _) => ScheduleStatusRefresh();
        session.Settings.Changed += ScheduleStatusRefresh;
    }

    public void ResetVault()
    {
        CloseMainWindow();
        Session?.Dispose();
        Session = null;
        Vault.Wipe(VaultDirectory);
        IsLocked = true;
        RefreshTrayStatus();
    }

    public void LockNow()
    {
        if (Session is null)
            return;
        IsLocked = true;
        SecureClipboard.ClearIfOwned();
        // Dialogs (thresholds, suppress, permission) close; the main window hides and forgets revealed values.
        foreach (var window in Windows.OfType<Window>().ToList())
            if (window is not UnlockWindow && window != _main)
                window.Close();
        _main?.LockAndHide();
        if (_tray is not null)
            _tray.ToolTipText = Shorten($"{AppInfo.Name} — locked (refresh continues)\n{(_status.Count > 0 ? $"{_status.Count} problem(s)" : "all good")}", 127);
    }

    private void CheckIdle()
    {
        if (Session is null || IsLocked)
            return;
        var minutes = Session.Settings.Settings.LockoutMinutes;
        if (minutes > 0 && IdleTime.Get() > TimeSpan.FromMinutes(minutes))
            LockNow();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && Session?.Settings.Settings.LockOnWindowsLock == true)
            Dispatcher.BeginInvoke(LockNow);
    }

    // ---------------- window ----------------

    public void Open(UiTarget target)
    {
        if (Session is null || IsLocked || target == UiTarget.None)
            return;
        _main ??= new MainWindow(Session);
        _main.OnStatusChanged(_status);
        _main.ShowTab(target);
    }

    private void CloseMainWindow()
    {
        if (_main is null)
            return;
        _main.AllowClose = true;
        _main.Close();
        _main = null;
    }

    /// <summary>Permission prompt for every call made with a target's elevated profile.</summary>
    private sealed class ElevationApprover(App app) : IElevationApprover
    {
        public Task<bool> ApproveAsync(ElevationRequest request, CancellationToken ct) =>
            app.Dispatcher.InvokeAsync(() =>
            {
                if (app.IsLocked)
                    return false;
                var dialog = new PermissionDialog(request);
                if (app._main is { IsVisible: true } owner)
                    dialog.Owner = owner;
                return dialog.ShowDialog() == true;
            }).Task;
    }

    /// <summary>
    /// Unencrypted crash details are written only when SKYPEEK_DEBUG=1, so normal runs keep all local data in the vault.
    /// </summary>
    public static void DebugLog(Exception? ex, string? context = null)
    {
        if (ex is null || Environment.GetEnvironmentVariable("SKYPEEK_DEBUG") != "1")
            return;
        try
        {
            var directory = Application.Current is App app ? app.VaultDirectory : Vault.DefaultDirectory;
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "debug.log"), $"{DateTime.Now:O} {context}\n{ex}\n\n");
        }
        catch
        {
            // ignore
        }
    }

    public void ExitApp()
    {
        SecureClipboard.ClearIfOwned();
        if (_main is not null)
            _main.AllowClose = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        Session?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
