using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Skypeek.Aws;
using Skypeek.Core;
using Skypeek.Core.Logging;
using Skypeek.Core.Services;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;
using Skypeek.Desktop.Updates;
using Skypeek.Desktop.Views;
using Skypeek.Storage;

namespace Skypeek.Desktop;

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
    private TrayIcon? _tray;
    private NativeMenuItem? _problemsItem;
    private NativeMenu? _trayMenu;
    private NativeMenuItem? _updateItem;
    private NativeMenuItemSeparator? _updateSeparator;
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _statusDebounce;
    private SessionLockMonitor? _lockMonitor;
    private UnlockWindow? _unlockWindow;
    private MainWindow? _main;
    private TrayStatus _status = TrayStatus.Empty;
    private int _failedAttempts;

    public static new App Current => (App)Application.Current!;

    /// <summary>The single-instance lock taken by <see cref="Program.Main"/> before the UI starts.</summary>
    public static SingleInstance? StartupInstance { get; set; }

    /// <summary>Set by the offscreen render harness: no tray, hotkey, single-instance or OS hooks.</summary>
    public static bool Headless { get; set; }

    public RequestLogService Log { get; } = new();
    public AppSession? Session { get; private set; }
    public bool IsLocked { get; private set; } = true;
    public string VaultDirectory { get; private set; } = Vault.DefaultDirectory;

    public IClassicDesktopStyleApplicationLifetime? Desktop => ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    /// <summary>The main window when it is shown (owner for dialogs).</summary>
    /// <summary>Update checks and installs (null in the render harness).</summary>
    public UpdateService? Updates { get; private set; }

    public Window? VisibleMain => _main is { IsVisible: true } main ? main : null;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // The system UI font where there is one everybody has (Segoe UI like the WPF app, San Francisco on macOS);
        // Linux desktops differ, so they keep the bundled Inter (Program.BuildAvaloniaApp).
        if (OperatingSystem.IsWindows())
            Resources["ContentControlThemeFontFamily"] = new FontFamily("Segoe UI");
        else if (OperatingSystem.IsMacOS())
            Resources["ContentControlThemeFontFamily"] = FontFamily.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (Headless || Desktop is not { } desktop)
            return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _instance = StartupInstance ?? SingleInstance.TryAcquire();
        if (_instance is null)
        {
            SingleInstance.SignalExisting("show");
            desktop.Shutdown();
            return;
        }
        _instance.Listen(_ => Dispatcher.UIThread.Post(() => RequestUi(UiTarget.Dashboard)));

        // One-time move from the old AwsManager name (vault folder and startup entry).
        VaultDirectory = Vault.ResolveDirectory();
        try
        {
            Autostart.MigrateLegacy();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Startup entry stays as it was; Settings → General can set it again.
        }

        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            DebugLog(args.Exception);
            args.Handled = true;
            _ = ConfirmDialog.InformAsync(VisibleMain, AppInfo.Name, args.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => DebugLog(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DebugLog(args.Exception);
            args.SetObserved();
        };

        AwsPipeline.Install(Log);

        // Any input in our windows counts as activity (the idle lock uses it where the OS has no idle time, e.g. Wayland).
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>((_, _) => IdleTime.NoteAppInput(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>((_, _) => IdleTime.NoteAppInput(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, _) => IdleTime.NoteAppInput(), RoutingStrategies.Tunnel, handledEventsToo: true);

        CreateTray();
        RegisterHotkey(Vault.ReadMeta(VaultDirectory)?.Hotkey ?? DefaultHotkey);

        DesktopNotifier.Initialize();
        DesktopNotifier.Activated += () => Dispatcher.UIThread.Post(() => RequestUi(UiTarget.Problems));
        Updates = new UpdateService(() => UpdateService.AutoCheckSetting(VaultDirectory),
            (title, message) => new DesktopNotifier(() => false).Notify(title, message));
        Updates.Changed += RefreshUpdateItem;
        RefreshUpdateItem();

        _lockMonitor = new SessionLockMonitor();
        _lockMonitor.Locked += () => Dispatcher.UIThread.Post(() =>
        {
            if (Session?.Settings.Settings.LockOnWindowsLock == true)
                LockNow();
        });
        _lockMonitor.Start();

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();

        // Without a tray (e.g. a Linux session without a status notifier host) the window is the only way in.
        var trayOnly = desktop.Args?.Contains("--tray", StringComparer.OrdinalIgnoreCase) == true && PlatformInfo.TrayAvailable;
        if (!Vault.Exists(VaultDirectory) || !trayOnly)
            RequestUi(UiTarget.Dashboard);
        else
            new DesktopNotifier(() => false).Notify($"{AppInfo.Name} is running",
                "Unlock it (the tray icon or the hotkey) to start background refresh.");
    }

    // ---------------- tray ----------------

    private void CreateTray()
    {
        var menu = _trayMenu = new NativeMenu();
        _problemsItem = Item("Problems (0)", () => RequestUi(UiTarget.Problems));
        menu.Items.Add(_problemsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Search…", () => RequestUi(UiTarget.Search)));
        menu.Items.Add(Item("Dashboard", () => RequestUi(UiTarget.Dashboard)));
        menu.Items.Add(Item("Request log", () => RequestUi(UiTarget.RequestLog)));
        menu.Items.Add(Item("Refresh all", () => { if (Session is not null) Session.Scheduler.RunNow(); else RequestUi(UiTarget.None); }));
        menu.Items.Add(Item("Settings", () => RequestUi(UiTarget.Settings)));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Lock now", LockNow));
        menu.Items.Add(Item("Exit", ExitApp));

        _tray = new TrayIcon
        {
            ToolTipText = $"{AppInfo.Name} — locked",
            Menu = menu,
            Icon = IconRenderer.Create(false, 0, locked: true),
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => RequestUi(UiTarget.Dashboard);
        TrayIcon.SetIcons(this, [_tray]);

        static NativeMenuItem Item(string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => action();
            return item;
        }
    }

    /// <summary>A "restart to install" item at the top of the tray menu while a downloaded update waits.</summary>
    private void RefreshUpdateItem()
    {
        if (_trayMenu is null || Updates is null)
            return;
        var ready = Updates.Stage == UpdateStage.Ready;
        if (ready && _updateItem is null)
        {
            _updateItem = new NativeMenuItem();
            _updateItem.Click += (_, _) => Updates.RestartToUpdate();
            _updateSeparator = new NativeMenuItemSeparator();
            _trayMenu.Items.Insert(0, _updateSeparator);
            _trayMenu.Items.Insert(0, _updateItem);
        }
        else if (!ready && _updateItem is not null)
        {
            _trayMenu.Items.Remove(_updateItem);
            _trayMenu.Items.Remove(_updateSeparator!);
            _updateItem = null;
            _updateSeparator = null;
        }
        if (_updateItem is not null)
            _updateItem.Header = $"Restart to install {AppInfo.Name} {Updates.NewVersion}";
    }

    private void ScheduleStatusRefresh()
    {
        Dispatcher.UIThread.Post(() =>
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
            _statusDebounce.Stop();
            _statusDebounce.Start();
        });
    }

    private void RefreshTrayStatus()
    {
        if (Session is null)
        {
            if (_tray is not null)
            {
                _tray.Icon = IconRenderer.Create(false, 0, locked: true);
                _tray.ToolTipText = $"{AppInfo.Name} — locked";
            }
            return;
        }

        _status = Session.ComputeTrayStatus();
        if (_tray is not null)
        {
            _tray.Icon = IconRenderer.Create(_status.IsRed, _status.Count);
            var tip = _status.Count == 0
                ? $"{AppInfo.Name} — all good"
                : $"{AppInfo.Name} — {_status.Count} problem(s)\n" + string.Join("\n", _status.Problems.Take(5).Select(p => Shorten(p.Display, 60)));
            _tray.ToolTipText = Shorten(tip, 127);
        }
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
                _hotkey = GlobalHotkey.Create();
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
        var session = await AppSession.StartAsync(vault, Log, new DesktopNotifier(() => IsLocked), new ElevationApprover(this));
        AttachSession(session);
    }

    /// <summary>Also used by the render harness, which builds a session from a scratch vault.</summary>
    public void AttachSession(AppSession session)
    {
        Session = session;
        IsLocked = false;
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
        _ = SecureClipboard.ClearIfOwnedAsync();
        // Dialogs (thresholds, suppress, permission) close; the main window hides and forgets revealed values.
        foreach (var window in Desktop?.Windows.ToList() ?? [])
            if (window is not UnlockWindow && window != _main)
                window.Close();
        _main?.LockAndHide();
        if (!PlatformInfo.TrayAvailable)
            ShowUnlock(UiTarget.Dashboard); // nothing else would bring the app back
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

    /// <summary>The main window (created if needed), for the render harness.</summary>
    public MainWindow EnsureMainWindow() => _main ??= new MainWindow(Session!);

    /// <summary>Permission prompt for every call made with a target's elevated profile.</summary>
    private sealed class ElevationApprover(App app) : IElevationApprover
    {
        public Task<bool> ApproveAsync(ElevationRequest request, CancellationToken ct) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (app.IsLocked)
                    return false;
                return await Dialogs.ShowAsync(new PermissionDialog(request), app.VisibleMain);
            });
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
        _ = SecureClipboard.ClearIfOwnedAsync();
        if (_main is not null)
            _main.AllowClose = true;
        _lockMonitor?.Dispose();
        Updates?.Dispose();
        Session?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        Desktop?.Shutdown();
    }
}
