using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Skypeek.App.Infrastructure;
using Skypeek.Core.Health;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.App.Views;

/// <summary>Tree dashboard: target (region) → Elastic Beanstalk / ECS / RDS / ElastiCache / alarms, with a details panel.</summary>
public partial class DashboardView
{
    private readonly AppSession _session;
    private readonly DispatcherTimer _debounce;
    private readonly Dictionary<string, bool> _expanded = new();
    /// <summary>EB application versions by <see cref="EbApplicationDetail.Key"/>; loaded when an application is shown.</summary>
    private readonly Dictionary<string, (DateTime LoadedUtc, IReadOnlyList<EbApplicationVersion> Versions)> _versions = new();
    private static readonly TimeSpan VersionsMaxAge = TimeSpan.FromMinutes(10);
    private string? _selectedKey;
    private bool _rebuilding;
    private bool _searching;

    public DashboardView(AppSession session)
    {
        InitializeComponent();
        _session = session;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Rebuild();
        };

        // Two-way IsExpanded/IsSelected on top of the themed TreeViewItem style (looked up at runtime).
        var style = new Style(typeof(TreeViewItem), TryFindResource(typeof(TreeViewItem)) as Style);
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(DashNode.IsExpanded)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(DashNode.IsSelected)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.ForegroundProperty, FindResource("TextNormal")));
        Tree.ItemContainerStyle = style;
        GroupByApp.IsChecked = session.Settings.Settings.EbGroupByApplication;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        };

        session.Health.Changed += Schedule;
        session.Monitor.StatusChanged += Schedule;
        session.Scheduler.JobCompleted += OnJobCompleted;
        session.Settings.Changed += Schedule;
        Rebuild();
    }

    public void Detach()
    {
        _session.Health.Changed -= Schedule;
        _session.Monitor.StatusChanged -= Schedule;
        _session.Scheduler.JobCompleted -= OnJobCompleted;
        _session.Settings.Changed -= Schedule;
    }

    public void ShowProblems()
    {
        ProblemsOnly.IsChecked = true;
        Rebuild();
        ExpandProblems();
    }

    private void OnJobCompleted(Target t, JobKind k, JobState s) => Schedule();

    private void Schedule() => Dispatcher.BeginInvoke(() =>
    {
        _debounce.Stop();
        _debounce.Start();
    });

    private void OnFilterChanged(object sender, RoutedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            var roots = DashboardTreeBuilder.Build(_session, ProblemsOnly.IsChecked == true);
            var query = SearchBox.Text?.Trim() ?? "";
            _searching = query.Length > 0;
            // While searching the tree shows only matches and the path to them, opened; normal expansion comes back after.
            if (_searching)
                roots = DashboardTreeBuilder.Filter(roots, query);
            DashNode? selected = null;
            foreach (var node in Flatten(roots))
            {
                if (!_searching)
                    node.IsExpanded = _expanded.TryGetValue(node.Key, out var open) ? open : DefaultExpanded(node);
                if (node.Key == _selectedKey)
                {
                    node.IsSelected = true;
                    selected = node;
                }
                node.PropertyChanged += OnNodeChanged;
            }
            Tree.ItemsSource = roots;
            if (selected is not null)
                ShowDetails(selected.Payload);

            var status = _session.ComputeTrayStatus();
            var suppressed = _session.Health.Snapshot().Sum(h => HealthRules.AllAlarms(h).Count(a => a.IsActive && a.Suppressed));
            Summary.Text = status.Count == 0 ? "All good" : $"{status.Count} problem(s)";
            if (suppressed > 0)
                Summary.Text += $" · {suppressed} suppressed alarm(s) ignored";
            Summary.Foreground = status.Count == 0 ? LevelToBrushConverter.Ok : LevelToBrushConverter.Critical;
            if (_searching)
                Summary.Text = $"{Flatten(roots).Count(n => n.IsMatch)} match(es) · {Summary.Text}";

            var health = _session.Health.Snapshot();
            var lastHealth = health.Select(h => h.HealthUpdated).Where(d => d is not null).DefaultIfEmpty().Max();
            var lastMetrics = health.Select(h => h.MetricsUpdated).Where(d => d is not null).DefaultIfEmpty().Max();
            Footer.Text = $"Health updated {Format(lastHealth)} · metrics updated {Format(lastMetrics)} · credentials file: {_session.Monitor.CredentialsPath}";
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>First time a node is seen: open targets, and anything that contains problems.</summary>
    private static bool DefaultExpanded(DashNode node) => node.Kind switch
    {
        NodeKind.Target => true,
        NodeKind.Group or NodeKind.EcsCluster or NodeKind.EbApplication or NodeKind.RdsCluster => node.ProblemCount > 0 && node.Children.Count <= 60,
        _ => false,
    };

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rebuilding || _searching || sender is not DashNode node)
            return;
        if (e.PropertyName == nameof(DashNode.IsExpanded))
            _expanded[node.Key] = node.IsExpanded;
    }

    private void OnSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_rebuilding)
            return;
        if (e.NewValue is DashNode node)
        {
            _selectedKey = node.Key;
            ShowDetails(node.Payload);
        }
    }

    private void ShowDetails(object? payload)
    {
        Details.Content = payload;
        if (payload is EbApplicationDetail app)
            _ = LoadVersionsAsync(app, force: false);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter)
        {
            Rebuild();
            // Jump to the first match.
            if (Tree.ItemsSource is IEnumerable<DashNode> roots && Flatten(roots).FirstOrDefault(n => n.IsMatch) is { } first)
                first.IsSelected = true;
            e.Handled = true;
        }
    }

    private void OnGroupByAppChanged(object sender, RoutedEventArgs e)
    {
        var settings = _session.Settings.Settings;
        var byApp = GroupByApp.IsChecked == true;
        if (settings.EbGroupByApplication != byApp)
        {
            settings.EbGroupByApplication = byApp;
            _session.Settings.SaveSettings(settings);
        }
        Rebuild();
    }

    private static IEnumerable<DashNode> Flatten(IEnumerable<DashNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children))
                yield return c;
        }
    }

    private void OnExpandProblems(object sender, RoutedEventArgs e) => ExpandProblems();

    private void ExpandProblems()
    {
        if (Tree.ItemsSource is not IEnumerable<DashNode> roots)
            return;
        foreach (var node in Flatten(roots))
        {
            var open = node.Children.Count > 0 && node.ProblemCount > 0;
            node.IsExpanded = open;
            _expanded[node.Key] = open;
        }
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        if (Tree.ItemsSource is not IEnumerable<DashNode> roots)
            return;
        foreach (var node in Flatten(roots))
        {
            node.IsExpanded = false;
            _expanded[node.Key] = false;
        }
    }

    private static string Format(DateTime? utc) => utc is null ? "never" : utc.Value.ToLocalTime().ToString("g");

    // ---------------- actions ----------------

    private void OnRefreshHealth(object sender, RoutedEventArgs e) => _session.Scheduler.RunNow(kind: JobKind.Health);
    private void OnRefreshMetrics(object sender, RoutedEventArgs e) => _session.Scheduler.RunNow(kind: JobKind.Metrics);
    private void OnRefreshCatalogs(object sender, RoutedEventArgs e) => _session.Scheduler.RunNow(kind: JobKind.Catalog);

    private void OnRefreshTarget(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long id })
            _session.Scheduler.RunNow(id);
    }

    private static ResourceStatus? ResourceFrom(object sender) => (sender as FrameworkElement)?.DataContext as ResourceStatus;

    private void OnOpenConsole(object sender, RoutedEventArgs e)
    {
        if (ResourceFrom(sender) is { } r)
            OpenUrl(r.ConsoleUrl);
    }

    /// <summary>Re-reads just this environment/service (state + CPU/memory) instead of waiting for the target-wide poll.</summary>
    private async void OnRefreshResource(object sender, RoutedEventArgs e)
    {
        if (ResourceFrom(sender) is not { } r || _session.Settings.FindTarget(r.TargetId) is not { } target)
            return;
        var button = sender as UIElement;
        if (button is not null)
            button.IsEnabled = false;
        ActionStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextNormal");
        ActionStatus.Text = $"Refreshing {r.DisplayName}…";
        try
        {
            await _session.Health.RefreshResourceAsync(target, r, CancellationToken.None);
            ActionStatus.Text = $"{r.DisplayName} refreshed at {DateTime.Now:T}.";
        }
        catch (Exception ex)
        {
            ActionStatus.Foreground = LevelToBrushConverter.Critical;
            ActionStatus.Text = $"Refresh of {r.DisplayName} failed: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}";
        }
        finally
        {
            // The details panel is rebuilt with the new data; the old button may already be gone.
            if (button is not null)
                button.IsEnabled = true;
        }
    }

    private void OnViewLogs(object sender, RoutedEventArgs e)
    {
        if (ResourceFrom(sender) is { } r && _session.Settings.FindTarget(r.TargetId) is { } target && Window.GetWindow(this) is MainWindow main)
            main.OpenLogs(target, r);
    }

    private void OnOpenAlarm(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AlarmDetail a)
            OpenUrl(a.ConsoleUrl);
    }

    private void OnCopyResource(object sender, RoutedEventArgs e)
    {
        if (ResourceFrom(sender) is { } r)
            SecureClipboard.CopyPlain(r is EcsServiceStatus ecs ? ecs.Snapshot.ServiceName : r.DisplayName);
    }

    /// <summary>Copies the button's Tag (a host name or endpoint).</summary>
    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string text } || string.IsNullOrWhiteSpace(text))
            return;
        SecureClipboard.CopyPlain(text);
        ActionStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextNormal");
        ActionStatus.Text = $"Copied {text}";
    }

    // ---------------- EB application versions ----------------

    private void OnShowApplication(object sender, RoutedEventArgs e)
    {
        if (OwningEnvironment(sender) is not var (target, env)
            || DashboardTreeBuilder.ApplicationDetail(_session, target.Id, env.Snapshot.ApplicationName) is not { } detail)
            return;
        _selectedKey = null;
        ShowDetails(detail);
    }

    private void OnReloadVersions(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EbApplicationDetail detail)
            _ = LoadVersionsAsync(detail, force: true);
    }

    private async Task LoadVersionsAsync(EbApplicationDetail detail, bool force)
    {
        if (!force && _versions.TryGetValue(detail.Key, out var cached) && DateTime.UtcNow - cached.LoadedUtc < VersionsMaxAge)
        {
            detail.SetVersions(cached.Versions, cached.LoadedUtc);
            return;
        }
        detail.VersionsStatus = "Loading versions…";
        try
        {
            var versions = await _session.Gateway.GetEbApplicationVersionsAsync(detail.Target, detail.Name, CancellationToken.None);
            var now = DateTime.UtcNow;
            _versions[detail.Key] = (now, versions);
            detail.SetVersions(versions, now);
        }
        catch (Exception ex)
        {
            detail.VersionsStatus = $"Could not load versions: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}";
        }
    }

    private void OnSetThresholds(object sender, RoutedEventArgs e)
    {
        if (ResourceFrom(sender) is not { } r || _session.Settings.FindTarget(r.TargetId) is not { } target)
            return;

        var settings = _session.Settings.Settings;
        var current = HealthRules.ResolveThresholds(settings, target, r);
        var (cpuLabel, memoryLabel) = r switch
        {
            RdsInstanceStatus => ("CPU", "Connections % of max"),
            CacheStatus => ("Engine CPU", "Memory used"),
            _ => ("CPU", "Memory"),
        };
        (double, double)? storage = r is RdsInstanceStatus { Snapshot.IsAurora: false } ? HealthRules.ResolveStorageThresholds(settings, r.ResourceKey) : null;
        var dialog = new ThresholdDialog($"{target.DisplayName}: {r.DisplayName}", current, settings.ResourceThresholds.ContainsKey(r.ResourceKey), cpuLabel, memoryLabel, storage)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true)
            return;

        if (dialog.Result is { } result)
            settings.ResourceThresholds[r.ResourceKey] = result;
        else
            settings.ResourceThresholds.Remove(r.ResourceKey);
        _session.Settings.SaveSettings(settings);
        _session.Health.Reevaluate();
    }

    /// <summary>"Suppress…" next to an alarm inside a resource's details.</summary>
    private void OnSuppressAlarm(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AlarmInfo alarm } element
            && FindOwningTarget(element) is { } target)
            Suppress(target, alarm);
    }

    private void OnSuppressAlarmDetail(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AlarmDetail a)
            Suppress(a.Target, a.Alarm);
    }

    private Target? FindOwningTarget(FrameworkElement element) =>
        FindOwningResource(element) is { } r ? _session.Settings.FindTarget(r.TargetId) : null;

    /// <summary>Rows (alarms, causes, nodes) sit inside a resource template whose DataContext is the resource.</summary>
    private static ResourceStatus? FindOwningResource(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: ResourceStatus r })
                return r;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private (Target Target, EbEnvironmentStatus Env)? OwningEnvironment(object sender) =>
        sender is FrameworkElement element && FindOwningResource(element) is EbEnvironmentStatus env && _session.Settings.FindTarget(env.TargetId) is { } target
            ? (target, env)
            : null;

    // ---------------- EB health cause suppression ----------------

    private void OnSuppressCause(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CauseItem cause } || OwningEnvironment(sender) is not var (target, env))
            return;
        var dialog = new CauseSuppressDialog(target, env.Snapshot.EnvironmentName, cause.Text) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } rule)
            return;
        var settings = _session.Settings.Settings;
        if (!settings.SuppressedCauses.Any(s => s.Pattern == rule.Pattern && s.TargetId == rule.TargetId && s.EnvironmentName == rule.EnvironmentName))
            settings.SuppressedCauses.Add(rule);
        _session.Settings.SaveSettings(settings);
        _session.Health.Reevaluate();
    }

    private void OnUnsuppressCause(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CauseItem cause } || OwningEnvironment(sender) is not var (target, env))
            return;
        var settings = _session.Settings.Settings;
        var removed = settings.SuppressedCauses.RemoveAll(rule =>
            (rule.TargetId is null || rule.TargetId == target.Id) &&
            (rule.EnvironmentName is null || rule.EnvironmentName == env.Snapshot.EnvironmentName) &&
            HealthRules.WildcardMatch(HealthRules.NormalizeCause(rule.Pattern), HealthRules.NormalizeCause(cause.Text)));
        if (removed == 0)
            return;
        _session.Settings.SaveSettings(settings);
        _session.Health.Reevaluate();
    }

    // ---------------- suppressions list ----------------

    private void OnShowSuppressions(object sender, RoutedEventArgs e) => ShowSuppressions();

    private void ShowSuppressions()
    {
        _selectedKey = null;
        Details.Content = SuppressionsDetail.From(_session.Settings.Settings, _session.Settings.Targets);
    }

    private void OnRemoveSuppression(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SuppressionRow row })
            return;
        var settings = _session.Settings.Settings;
        switch (row.Rule)
        {
            case AlarmSuppression a: settings.SuppressedAlarms.Remove(a); break;
            case CauseSuppression c: settings.SuppressedCauses.Remove(c); break;
        }
        _session.Settings.SaveSettings(settings);
        _session.Health.Reevaluate();
        ShowSuppressions();
    }

    // ---------------- EB actions (elevated key, confirmed per call) ----------------

    private void OnDeployLatest(object sender, RoutedEventArgs e)
    {
        if (OwningEnvironment(sender) is var (target, env) && env.Snapshot.LatestVersionLabel is { } latest)
            _ = RunActionAsync(target, $"Deploying {latest} to {env.Snapshot.EnvironmentName}",
                () => _session.Gateway.DeployEbVersionAsync(target, env.Snapshot, latest, CancellationToken.None));
    }

    private void OnRedeploy(object sender, RoutedEventArgs e)
    {
        if (OwningEnvironment(sender) is var (target, env) && env.Snapshot.VersionLabel is { } current)
            _ = RunActionAsync(target, $"Redeploying {current} to {env.Snapshot.EnvironmentName}",
                () => _session.Gateway.DeployEbVersionAsync(target, env.Snapshot, current, CancellationToken.None));
    }

    private void OnRestartAppServers(object sender, RoutedEventArgs e)
    {
        if (OwningEnvironment(sender) is var (target, env))
            _ = RunActionAsync(target, $"Restarting app servers of {env.Snapshot.EnvironmentName}",
                () => _session.Gateway.RestartEbAppServersAsync(target, env.Snapshot, CancellationToken.None));
    }

    private void OnRebootNode(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string instanceId } && OwningEnvironment(sender) is var (target, env))
            _ = RunActionAsync(target, $"Rebooting {instanceId}",
                () => _session.Gateway.RebootEbInstanceAsync(target, env.Snapshot, instanceId, CancellationToken.None));
    }

    private void OnTerminateNode(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string instanceId } && OwningEnvironment(sender) is var (target, env))
            _ = RunActionAsync(target, $"Terminating {instanceId}",
                () => _session.Gateway.TerminateEbInstanceAsync(target, env.Snapshot, instanceId, CancellationToken.None));
    }

    private void OnOpenDetailConsole(object sender, RoutedEventArgs e)
    {
        switch ((sender as FrameworkElement)?.DataContext)
        {
            case EcsTaskDetail task: OpenUrl(task.ConsoleUrl); break;
            case EcsContainerDetail container: OpenUrl(container.ConsoleUrl); break;
        }
    }

    /// <summary>Opens the Logs tab on this container's CloudWatch stream for this one task.</summary>
    private void OnViewContainerLogs(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EcsContainerInfo container } element)
            return;
        (EcsServiceStatus? service, EcsTaskInfo? task) = element.DataContext switch
        {
            EcsContainerDetail d => (d.Service, d.Task),
            EcsContainerInfo when FindDetail<EcsTaskDetail>(element) is { } t => (t.Service, t.Task),
            _ => (null, null),
        };
        if (service is null || task is null || _session.Settings.FindTarget(service.TargetId) is not { } target || Window.GetWindow(this) is not MainWindow main)
            return;
        main.OpenLogs(target, service, new LogFocus(container.Name, task.TaskId));
    }

    private static T? FindDetail<T>(DependencyObject? current) where T : class
    {
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: T found })
                return found;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>Runs the AWS CLI sign-in in its own console; the token cache watcher resumes the profile afterwards.</summary>
    private void OnSsoSignIn(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SsoSignInDetail detail)
            return;
        try
        {
            // A console program started from this GUI app gets its own console window.
            var start = new ProcessStartInfo("aws") { UseShellExecute = false, CreateNoWindow = false };
            start.ArgumentList.Add("sso");
            start.ArgumentList.Add("login");
            start.ArgumentList.Add("--profile");
            start.ArgumentList.Add(detail.Profile);
            Process.Start(start);
            ActionStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextNormal");
            ActionStatus.Text = $"Approve the sign-in in your browser; {detail.Profile} resumes automatically afterwards.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ActionStatus.Foreground = LevelToBrushConverter.Critical;
            ActionStatus.Text = $"The AWS CLI (aws) was not found. Install it, or run: {detail.Command}";
        }
    }

    private void OnForceNewDeployment(object sender, RoutedEventArgs e)
    {
        if (ResourceFrom(sender) is EcsServiceStatus svc && _session.Settings.FindTarget(svc.TargetId) is { } target)
            _ = RunActionAsync(target, $"Forcing a new deployment of {svc.Snapshot.ServiceName}",
                () => _session.Gateway.ForceNewEcsDeploymentAsync(target, svc.Snapshot, CancellationToken.None));
    }

    /// <summary>Runs an approved action, reports the outcome, then refreshes health so EB/ECS events show progress.</summary>
    private async Task RunActionAsync(Target target, string description, Func<Task> action)
    {
        ActionStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextNormal");
        ActionStatus.Text = $"{description}: waiting for your confirmation…";
        try
        {
            await action();
            ActionStatus.Text = $"{description}: request accepted by AWS. Progress appears in Recent events; health refreshes shortly.";
            ActionStatus.Foreground = LevelToBrushConverter.Ok;
            _session.Scheduler.RunNow(target.Id, JobKind.Health);
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => _session.Scheduler.RunNow(target.Id, JobKind.Health), TaskScheduler.Default);
        }
        catch (Core.ElevationDeniedException)
        {
            ActionStatus.Text = $"{description}: not approved; nothing was sent to AWS.";
        }
        catch (Exception ex)
        {
            ActionStatus.Text = $"{description} failed: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}";
            ActionStatus.Foreground = LevelToBrushConverter.Critical;
        }
    }

    private void Suppress(Target target, AlarmInfo alarm)
    {
        var dialog = new SuppressDialog(_session, target, alarm.Name) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } rule)
            return;
        var settings = _session.Settings.Settings;
        if (!settings.SuppressedAlarms.Any(s => s.Pattern.Equals(rule.Pattern, StringComparison.OrdinalIgnoreCase) && s.TargetId == rule.TargetId))
            settings.SuppressedAlarms.Add(rule);
        _session.Settings.SaveSettings(settings);
        _session.Health.Reevaluate();
    }

    private void OnUnsuppressAlarm(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AlarmDetail a)
            return;
        var settings = _session.Settings.Settings;
        var removed = settings.SuppressedAlarms.RemoveAll(s =>
            (s.TargetId is null || s.TargetId == a.Target.Id) && HealthRules.WildcardMatch(s.Pattern, a.Alarm.Name));
        if (removed == 0)
            return;
        _session.Settings.SaveSettings(settings);
        _session.Health.Reevaluate();
    }

    public static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.EndsWith(".aws.amazon.com", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
