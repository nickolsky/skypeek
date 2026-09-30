using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;
using System.ComponentModel;
using Skypeek.Core.Services;

namespace Skypeek.Desktop.Views;

/// <summary>The single application window: catalog, dashboard, request log and settings as tabs. Closing hides it to the tray.</summary>
public partial class MainWindow : Window
{
    private readonly CatalogView _catalog;
    private readonly DashboardView _dashboard;
    private readonly LogsView _logs;
    private readonly NetworkView _network;
    private readonly AppSession _session;
    private readonly RequestLogView _log;
    private readonly SettingsView _settings;

    public MainWindow(AppSession session)
    {
        InitializeComponent();
        _session = session;
        Title = AppInfo.Title;
        Icon = IconRenderer.WindowIcon();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        CatalogTab.Content = _catalog = new CatalogView(session);
        DashboardTab.Content = _dashboard = new DashboardView(session);
        LogsTab.Content = _logs = new LogsView(session);
        NetworkTab.Content = _network = new NetworkView(session);
        LogTab.Content = _log = new RequestLogView(session.Log);
        SettingsTab.Content = _settings = new SettingsView(session);
        session.Activity.Changed += OnActivityChanged;
        Closed += (_, _) => session.Activity.Changed -= OnActivityChanged;
        ShowActivity();
    }

    private void OnActivityChanged() => Dispatcher.UIThread.Post(ShowActivity);

    /// <summary>"Refreshing Prod: health, metrics and alarms (+2 queued)" next to Lock/Exit.</summary>
    private void ShowActivity()
    {
        var items = _session.Activity.Items;
        ActivityPanel.IsVisible = items.Count > 0;
        if (items.Count == 0)
            return;
        var running = items.Where(i => !i.Waiting).ToList();
        var queued = items.Count - running.Count;
        var shown = running.Count > 0 ? running : items.ToList();
        var text = string.Join("; ", shown.GroupBy(i => i.TargetName).Select(g => $"{g.Key}: {string.Join(", ", g.Select(i => i.What).Distinct())}"));
        ActivityText.Text = $"Refreshing {text}{(queued > 0 && running.Count > 0 ? $" (+{queued} queued)" : "")}";
        ToolTip.SetTip(ActivityPanel, string.Join(Environment.NewLine, items.Select(i => i.Text)));
    }

    /// <summary>Set when the app exits or the session ends; otherwise closing only hides the window.</summary>
    public bool AllowClose { get; set; }

    public void ShowTab(UiTarget target)
    {
        Tabs.SelectedItem = target switch
        {
            UiTarget.Dashboard or UiTarget.Problems => DashboardTab,
            UiTarget.RequestLog => LogTab,
            UiTarget.Settings => SettingsTab,
            _ => CatalogTab,
        };
        if (target == UiTarget.Problems)
            _dashboard.ShowProblems();

        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        if (target == UiTarget.Search)
            Dispatcher.UIThread.Post(_catalog.FocusSearch);
    }

    /// <summary>"View logs" from the dashboard.</summary>
    public void OpenLogs(Core.Models.Target target, Core.Models.ResourceStatus resource, LogFocus? focus = null)
    {
        Tabs.SelectedItem = LogsTab;
        _logs.Open(target, resource, focus);
    }

    /// <summary>Any Network tab node by key (e.g. a subnet from the reach check).</summary>
    public void OpenNetworkNode(string key)
    {
        Tabs.SelectedItem = NetworkTab;
        _network.Reveal(key);
    }

    /// <summary>"Analyze reach", starting from a resource or interface (by key) when given.</summary>
    public void OpenReach(string? fromKey = null)
    {
        var dialog = new ReachDialog(_session, fromKey);
        dialog.Show(this);
    }

    /// <summary>A security group chip on the dashboard: show it on the Network tab.</summary>
    public void OpenNetwork(Core.Models.Target target, string securityGroupId)
    {
        Tabs.SelectedItem = NetworkTab;
        _network.Open(target, securityGroupId);
    }

    /// <summary>An EC2 instance from the Network tab or a load balancer target: select it (or its EB environment) on the dashboard.</summary>
    public bool RevealInstance(Core.Models.Target target, string instanceId)
    {
        if (_session.Health.Get(target.Id) is not { } health || DashboardTreeBuilder.InstanceKey(health, instanceId) is not { } key)
            return false;
        Tabs.SelectedItem = DashboardTab;
        _dashboard.Reveal(key);
        return true;
    }

    /// <summary>A resource from the Network tab ("used by"): select it on the dashboard.</summary>
    public void RevealResource(string resourceKey)
    {
        Tabs.SelectedItem = DashboardTab;
        _dashboard.Reveal(resourceKey);
    }

    public void OnStatusChanged(TrayStatus status)
    {
        DashboardTab.Header = status.Count == 0 ? "Dashboard" : $"Dashboard ({status.Count})";
        Title = status.Count == 0 ? AppInfo.Title : $"{AppInfo.Title} — {status.Count} problem(s)";
        _dashboard.Refresh();
    }

    /// <summary>On lock: drop revealed values and hide.</summary>
    public void LockAndHide()
    {
        _catalog.Wipe();
        _logs.Wipe();
        _network.Wipe();
        UsagePanel.ClearCache();
        ListenersPanel.ClearCache();
        BuildHistoryPanel.ClearCache();
        StackHistoryPanel.ClearCache();
        HistoryPanel.ClearCache();
        Hide();
    }

    private void OnLock(object? sender, RoutedEventArgs e) => App.Current.LockNow();

    private async void OnExit(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmDialog.AskAsync(this, "Exit Skypeek?", "Background refresh and notifications stop until you start it again.", "Exit"))
            App.Current.ExitApp();
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs && Tabs.SelectedItem == SettingsTab)
            _settings.OnShown();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            ShowTab(UiTarget.Search);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Tabs.SelectedItem == CatalogTab)
        {
            Hide();
            e.Handled = true;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            _catalog.Wipe();
            // Closing hides the window to the tray; with no tray it stays on the taskbar, minimized.
            if (PlatformInfo.TrayAvailable)
                Hide();
            else
                WindowState = WindowState.Minimized;
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _catalog.Detach();
        _dashboard.Detach();
        _network.Detach();
        _log.Detach();
        base.OnClosed(e);
    }
}
