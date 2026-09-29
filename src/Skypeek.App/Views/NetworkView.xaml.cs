using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Skypeek.App.Infrastructure;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.App.Views;

/// <summary>
/// Network tab: VPCs → subnets → IP addresses, and security groups with their rules (cached per target). Rule edits
/// go through the gateway's approved actions and refresh just that group.
/// </summary>
public partial class NetworkView
{
    private readonly AppSession _session;
    private readonly DispatcherTimer _debounce;
    private readonly Dictionary<string, bool> _expanded = new();
    private string? _selectedKey;
    private string? _revealKey;
    private bool _rebuilding;
    private bool _searching;

    public NetworkView(AppSession session)
    {
        InitializeComponent();
        _session = session;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Rebuild();
        };

        var style = new Style(typeof(TreeViewItem), TryFindResource(typeof(TreeViewItem)) as Style);
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(DashNode.IsExpanded)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(DashNode.IsSelected)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.ForegroundProperty, FindResource("TextNormal")));
        Tree.ItemContainerStyle = style;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        };

        session.Network.Changed += Schedule;
        session.Scheduler.JobCompleted += OnJobCompleted;
        session.Settings.Changed += Schedule;
        Rebuild();
    }

    public void Detach()
    {
        _session.Network.Changed -= Schedule;
        _session.Scheduler.JobCompleted -= OnJobCompleted;
        _session.Settings.Changed -= Schedule;
    }

    /// <summary>On lock: clear the selection and search.</summary>
    public void Wipe()
    {
        SearchBox.Text = "";
        Details.Content = null;
        _selectedKey = null;
    }

    /// <summary>Opens a security group (from an EC2 instance or load balancer on the dashboard).</summary>
    public void Open(Target target, string securityGroupId)
    {
        var key = NetworkTreeBuilder.SecurityGroupKey(target.Id, securityGroupId);
        _revealKey = key;
        SearchBox.Text = "";
        if (_session.Network.Get(target.Id) is not { } snapshot || snapshot.SecurityGroups.All(g => g.Id != securityGroupId))
        {
            ActionStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextNormal");
            ActionStatus.Text = $"{securityGroupId} is not in the downloaded data yet; downloading {target.DisplayName} now…";
            _session.Scheduler.RunNow(target.Id, JobKind.Network);
        }
        Rebuild();
    }

    private void OnJobCompleted(Target t, JobKind kind, JobState s)
    {
        if (kind == JobKind.Network)
            Schedule();
    }

    private void Schedule() => Dispatcher.BeginInvoke(() =>
    {
        _debounce.Stop();
        _debounce.Start();
    });

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            var roots = NetworkTreeBuilder.Build(_session);
            var query = SearchBox.Text?.Trim() ?? "";
            _searching = query.Length > 0;
            if (_searching)
                roots = DashboardTreeBuilder.Filter(roots, query);

            if (_revealKey is { } reveal && FindPath(roots, reveal) is { } path)
            {
                foreach (var ancestor in path.SkipLast(1))
                    _expanded[ancestor.Key] = true;
                _selectedKey = reveal;
                _revealKey = null;
            }

            DashNode? selected = null;
            foreach (var node in Flatten(roots))
            {
                if (!_searching)
                    node.IsExpanded = _expanded.TryGetValue(node.Key, out var open) ? open : node.Kind is NodeKind.Target or NodeKind.Vpc;
                if (node.Key == _selectedKey)
                {
                    node.IsSelected = true;
                    selected = node;
                }
                node.PropertyChanged += OnNodeChanged;
            }
            Tree.ItemsSource = roots;
            if (selected is not null)
                Details.Content = selected.Payload;

            var snapshots = _session.Network.Snapshot();
            Summary.Text = snapshots.Count == 0
                ? "Nothing downloaded yet."
                : $"{snapshots.Sum(s => s.Interfaces.Count)} interface(s), {snapshots.Sum(s => s.SecurityGroups.Count)} security group(s) · oldest download {snapshots.Min(s => s.DownloadedUtc).ToLocalTime():g}";
            if (_searching)
                Summary.Text = $"{Flatten(roots).Count(n => n.IsMatch)} match(es) · {Summary.Text}";
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_rebuilding && !_searching && sender is DashNode node && e.PropertyName == nameof(DashNode.IsExpanded))
            _expanded[node.Key] = node.IsExpanded;
    }

    private void OnSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_rebuilding || e.NewValue is not DashNode node)
            return;
        _selectedKey = node.Key;
        Details.Content = node.Payload;
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

    private static List<DashNode>? FindPath(IEnumerable<DashNode> nodes, string key)
    {
        foreach (var node in nodes)
        {
            if (node.Key == key)
                return [node];
            if (FindPath(node.Children, key) is { } below)
                return [node, .. below];
        }
        return null;
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
            if (Tree.ItemsSource is IEnumerable<DashNode> roots && Flatten(roots).FirstOrDefault(n => n.IsMatch) is { } first)
                first.IsSelected = true;
            e.Handled = true;
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

    // ---------------- actions ----------------

    private void OnDownloadAll(object sender, RoutedEventArgs e)
    {
        _session.Scheduler.RunNow(kind: JobKind.Network);
        Status("Downloading the network inventory of every target…");
        Schedule();
    }

    private void OnDownloadTarget(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long id })
        {
            _session.Scheduler.RunNow(id, JobKind.Network);
            Status("Downloading…");
            Schedule();
        }
    }

    private void OnOpenConsole(object sender, RoutedEventArgs e)
    {
        var url = (sender as FrameworkElement)?.DataContext switch
        {
            VpcDetail d => d.ConsoleUrl,
            SubnetDetail d => d.ConsoleUrl,
            InterfaceDetail d => d.ConsoleUrl,
            SecurityGroupDetail d => d.ConsoleUrl,
            _ => null,
        };
        if (url is not null)
            DashboardView.OpenUrl(url);
    }

    private void OnCopyText(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string text } || string.IsNullOrWhiteSpace(text))
            return;
        SecureClipboard.CopyPlain(text);
        Status($"Copied {text}");
    }

    /// <summary>A security group chip or rule source: select that group in the tree.</summary>
    private void OnSelectGroup(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string groupId } element || TargetOf(element) is not { } target)
            return;
        _revealKey = NetworkTreeBuilder.SecurityGroupKey(target.Id, groupId);
        SearchBox.Text = "";
        Rebuild();
    }

    private void OnShowInstance(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string instanceId } element || TargetOf(element) is not { } target || Window.GetWindow(this) is not MainWindow main)
            return;
        if (!main.RevealInstance(target, instanceId))
            Status($"{instanceId} is not on the dashboard (EC2 monitoring is off for this target, or it is hidden).");
    }

    private Target? TargetOf(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            var target = (current as FrameworkElement)?.DataContext switch
            {
                SecurityGroupDetail d => d.Target,
                InterfaceDetail d => d.Target,
                SubnetDetail d => d.Target,
                VpcDetail d => d.Target,
                _ => null,
            };
            if (target is not null)
                return target;
        }
        return null;
    }

    private SecurityGroupDetail? GroupDetailOf(object sender)
    {
        for (DependencyObject? current = sender as DependencyObject; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: SecurityGroupDetail d })
                return d;
        return null;
    }

    private async void OnRefreshGroup(object sender, RoutedEventArgs e)
    {
        if (GroupDetailOf(sender) is not { } d)
            return;
        Status($"Refreshing {d.Group.Name}…");
        try
        {
            await _session.Network.RefreshSecurityGroupsAsync(d.Target, [d.Group.Id], CancellationToken.None);
            Status($"{d.Group.Name} refreshed at {DateTime.Now:T}.");
        }
        catch (Exception ex)
        {
            Fail($"Refresh of {d.Group.Name} failed", ex);
        }
    }

    private (IReadOnlyList<SecurityGroupInfo> Groups, IReadOnlyList<string> Cidrs) VpcContext(SecurityGroupDetail d)
    {
        var snapshot = _session.Network.Get(d.Target.Id);
        var groups = snapshot?.SecurityGroups.Where(g => g.VpcId == d.Group.VpcId).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        var cidrs = snapshot?.Vpcs.FirstOrDefault(v => v.Id == d.Group.VpcId)?.Cidrs ?? [];
        return (groups, cidrs);
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        if (GroupDetailOf(sender) is not { } d || sender is not FrameworkElement { Tag: string direction })
            return;
        var (groups, cidrs) = VpcContext(d);
        var dialog = new RuleEditorDialog(d.Group, null, direction == "out", groups, cidrs) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } rule)
            return;
        _ = RunRuleActionAsync(d, $"Adding {rule.Summary} to {d.Group.Name}",
            () => _session.Gateway.AddSecurityGroupRuleAsync(d.Target, d.Group, rule, CancellationToken.None));
    }

    private void OnEditRule(object sender, RoutedEventArgs e)
    {
        if (GroupDetailOf(sender) is not { } d || sender is not FrameworkElement { Tag: SecurityGroupRuleInfo current })
            return;
        var (groups, cidrs) = VpcContext(d);
        var dialog = new RuleEditorDialog(d.Group, current, current.IsEgress, groups, cidrs) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Result is not { } updated)
            return;
        if (updated == current.ToSpec())
        {
            Status("Nothing changed.");
            return;
        }
        _ = RunRuleActionAsync(d, $"Changing rule {current.RuleId}",
            () => _session.Gateway.UpdateSecurityGroupRuleAsync(d.Target, d.Group, current, updated, CancellationToken.None));
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (GroupDetailOf(sender) is not { } d || sender is not FrameworkElement { Tag: SecurityGroupRuleInfo rule })
            return;
        // The permission dialog is the confirmation (it asks for the group ID).
        _ = RunRuleActionAsync(d, $"Deleting rule {rule.RuleId} ({rule.Summary})",
            () => _session.Gateway.DeleteSecurityGroupRuleAsync(d.Target, d.Group, rule, CancellationToken.None));
    }

    /// <summary>Runs an approved rule change, then re-reads the group so the tab shows what AWS now has.</summary>
    private async Task RunRuleActionAsync(SecurityGroupDetail d, string description, Func<Task> action)
    {
        Status($"{description}: waiting for your confirmation…");
        try
        {
            await action();
            Status($"{description}: done.", LevelToBrushConverter.Ok);
        }
        catch (Core.ElevationDeniedException)
        {
            Status($"{description}: not approved; nothing was sent to AWS.");
            return;
        }
        catch (Exception ex)
        {
            Fail($"{description} failed", ex);
        }
        try
        {
            await _session.Network.RefreshSecurityGroupsAsync(d.Target, [d.Group.Id], CancellationToken.None);
        }
        catch (Exception ex)
        {
            Fail($"Could not re-read {d.Group.Name}", ex);
        }
    }

    private void Status(string text, System.Windows.Media.Brush? brush = null)
    {
        ActionStatus.Foreground = brush ?? (System.Windows.Media.Brush)FindResource("TextNormal");
        ActionStatus.Text = text;
    }

    private void Fail(string what, Exception ex) =>
        Status($"{what}: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}", LevelToBrushConverter.Critical);
}
