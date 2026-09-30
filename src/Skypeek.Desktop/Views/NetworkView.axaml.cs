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
using Avalonia.VisualTree;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.Desktop.Views;

/// <summary>
/// Network tab: VPCs → subnets → IP addresses, and security groups with their rules (cached per target). Rule edits
/// go through the gateway's approved actions and refresh just that group.
/// </summary>
public partial class NetworkView : UserControl
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

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

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

    /// <summary>Selects any node by key (its parents open).</summary>
    public void Reveal(string key)
    {
        _revealKey = key;
        SearchBox.Text = "";
        Rebuild();
    }

    private void OnAnalyzeReach(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow main)
            main.OpenReach((sender as Control)?.Tag as string);
    }

    /// <summary>Opens a security group (from an EC2 instance or load balancer on the dashboard).</summary>
    public void Open(Target target, string securityGroupId)
    {
        var key = NetworkTreeBuilder.SecurityGroupKey(target.Id, securityGroupId);
        _revealKey = key;
        SearchBox.Text = "";
        if (_session.Network.Get(target.Id) is not { } snapshot || snapshot.SecurityGroups.All(g => g.Id != securityGroupId))
        {
            Status($"{securityGroupId} is not in the downloaded data yet; downloading {target.DisplayName} now…");
            _session.Scheduler.RunNow(target.Id, JobKind.Network);
        }
        Rebuild();
    }

    private void OnJobCompleted(Target t, JobKind kind, JobState s)
    {
        if (kind == JobKind.Network)
            Schedule();
    }

    private void Schedule() => Dispatcher.UIThread.Post(() =>
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
            var match = NetworkQuery.Parse(query);
            IncludeWorld.IsVisible = match?.Kind == NetworkQueryKind.Address;
            if (match is not null)
                roots = ShowMatches(roots, match);
            else
            {
                MatchPanel.IsVisible = false;
                if (_searching)
                    roots = DashboardTreeBuilder.Filter(roots, query);
            }

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
            {
                Tree.SelectedItem = selected;
                Details.Content = selected.Payload;
            }

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

    /// <summary>
    /// An IP, CIDR or sg-… search: the results panel says what has the address and which rules match (a rule for
    /// 10.0.0.0/16 matches 10.0.4.2 although the text differs); the tree keeps just those interfaces and groups.
    /// </summary>
    private List<DashNode> ShowMatches(List<DashNode> roots, NetworkQuery query)
    {
        var result = NetworkSearch.Search(_session.Network.Snapshot(), query, IncludeWorld.IsChecked == true);
        var targets = _session.Settings.Targets.ToDictionary(t => t.Id, t => t.DisplayName);
        string T(long id) => targets.GetValueOrDefault(id) ?? "?";
        var rows = new List<SearchRow>();
        foreach (var m in result.Interfaces)
            rows.Add(new SearchRow(m.Interface.PrivateIp ?? m.Interface.Id, $"{m.Interface.Owner} · {m.Interface.Id} · {m.Interface.SubnetId} · {T(m.TargetId)}",
                NetworkTreeBuilder.InterfaceKey(m.TargetId, m.Interface.Id)));
        foreach (var g in result.Groups)
            rows.Add(new SearchRow(g.Group.Title, $"{g.Group.Id} · {(query.Kind == NetworkQueryKind.Group ? "the group" : $"a group of {query.Text}")} · {T(g.TargetId)}",
                NetworkTreeBuilder.SecurityGroupKey(g.TargetId, g.Group.Id)));
        foreach (var r in result.Rules.OrderBy(r => r.Rule.IsEgress).ThenBy(r => r.Group.Name, StringComparer.OrdinalIgnoreCase))
            rows.Add(new SearchRow(r.Group.Title, r.Text, NetworkTreeBuilder.SecurityGroupKey(r.TargetId, r.Group.Id),
                r.Rule.IsOpenToWorld ? HealthLevel.Warn : HealthLevel.Ok));
        MatchList.ItemsSource = rows;
        MatchHeader.Text = query.Kind == NetworkQueryKind.Group
            ? $"{query.Text}: {result.Interfaces.Count} interface(s) use it, {result.Rules.Count} rule(s) in other groups allow it"
            : $"{query.Text}: {result.Interfaces.Count} interface(s) {(query.Net!.Value.IsSingleAddress ? "have this address" : "in this range")}, "
              + $"{result.Rules.Count} rule(s) match{(result.WorldRules > 0 && IncludeWorld.IsChecked != true ? $" ({result.WorldRules} more open to everyone, not shown)" : "")}";
        MatchPanel.IsVisible = true;

        var keys = rows.Select(r => r.Key).Where(k => k is not null).ToHashSet()!;
        var kept = new List<DashNode>();
        foreach (var root in roots)
            if (KeepKeys(root, keys!))
                kept.Add(root);
        return kept;
    }

    private static bool KeepKeys(DashNode node, HashSet<string> keys)
    {
        if (keys.Contains(node.Key))
        {
            node.IsMatch = true;
            node.Children.Clear();
            return true;
        }
        foreach (var child in node.Children.ToList())
            if (!KeepKeys(child, keys))
                node.Children.Remove(child);
        if (node.Children.Count == 0)
            return false;
        node.IsExpanded = true;
        return true;
    }

    private void OnIncludeWorldChanged(object? sender, RoutedEventArgs e) => Rebuild();

    private void OnMatchClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string key || Tree.ItemsSource is not IEnumerable<DashNode> roots || Flatten(roots).FirstOrDefault(n => n.Key == key) is not { } node)
            return;
        Tree.SelectedItem = node;
        _selectedKey = key;
        Details.Content = node.Payload;
    }

    private void OnShowResource(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string key } && TopLevel.GetTopLevel(this) is MainWindow main)
            main.RevealResource(key);
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_rebuilding && !_searching && sender is DashNode node && e.PropertyName == nameof(DashNode.IsExpanded))
            _expanded[node.Key] = node.IsExpanded;
    }

    private void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding || Tree.SelectedItem is not DashNode node)
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

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SearchBox.Text?.Length > 0)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Rebuild();
            if (Tree.ItemsSource is IEnumerable<DashNode> roots && Flatten(roots).FirstOrDefault(n => n.IsMatch) is { } first)
                Tree.SelectedItem = first;
            e.Handled = true;
        }
    }

    private void OnCollapseAll(object? sender, RoutedEventArgs e)
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

    private void OnDownloadAll(object? sender, RoutedEventArgs e)
    {
        _session.Scheduler.RunNow(kind: JobKind.Network);
        Status("Downloading the network inventory of every target…");
        Schedule();
    }

    private void OnDownloadTarget(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: long id })
        {
            _session.Scheduler.RunNow(id, JobKind.Network);
            Status("Downloading…");
            Schedule();
        }
    }

    private void OnOpenConsole(object? sender, RoutedEventArgs e)
    {
        var url = (sender as Control)?.DataContext switch
        {
            VpcDetail d => d.ConsoleUrl,
            SubnetDetail d => d.ConsoleUrl,
            InterfaceDetail d => d.ConsoleUrl,
            SecurityGroupDetail d => d.ConsoleUrl,
            GatewayDetail d => d.ConsoleUrl,
            _ => null,
        };
        if (url is not null)
            DashboardView.OpenUrl(url);
    }

    private void OnCopyText(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string text } || string.IsNullOrWhiteSpace(text))
            return;
        SecureClipboard.CopyPlain(text);
        Status($"Copied {text}");
    }

    /// <summary>A security group chip or rule source: select that group in the tree.</summary>
    private void OnSelectGroup(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string groupId } element || TargetOf(element) is not { } target)
            return;
        _revealKey = NetworkTreeBuilder.SecurityGroupKey(target.Id, groupId);
        SearchBox.Text = "";
        Rebuild();
    }

    private void OnShowInstance(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string instanceId } element || TargetOf(element) is not { } target || TopLevel.GetTopLevel(this) is not MainWindow main)
            return;
        if (!main.RevealInstance(target, instanceId))
            Status($"{instanceId} is not on the dashboard (EC2 monitoring is off for this target, or it is hidden).");
    }

    private Target? TargetOf(Visual element)
    {
        for (Visual? current = element; current is not null; current = current.GetVisualParent())
        {
            var target = (current as Control)?.DataContext switch
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

    private SecurityGroupDetail? GroupDetailOf(object? sender)
    {
        for (Visual? current = sender as Visual; current is not null; current = current.GetVisualParent())
            if (current is Control { DataContext: SecurityGroupDetail d })
                return d;
        return null;
    }

    private async void OnRefreshGroup(object? sender, RoutedEventArgs e)
    {
        if (GroupDetailOf(sender) is not { } d)
            return;
        Status($"Refreshing {d.Group.Name}…");
        try
        {
            using var activity = _session.Activity.Begin(d.Target.Id, d.Target.DisplayName, $"security group {d.Group.Name}");
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

    private async void OnAddRule(object? sender, RoutedEventArgs e)
    {
        if (GroupDetailOf(sender) is not { } d || sender is not Control { Tag: string direction })
            return;
        var (groups, cidrs) = VpcContext(d);
        var dialog = new RuleEditorDialog(d.Group, null, direction == "out", groups, cidrs);
        if (!await Dialogs.ShowAsync(dialog, Dialogs.OwnerOf(this)) || dialog.Result is not { } rule)
            return;
        _ = RunRuleActionAsync(d, $"Adding {rule.Summary} to {d.Group.Name}",
            () => _session.Gateway.AddSecurityGroupRuleAsync(d.Target, d.Group, rule, CancellationToken.None));
    }

    private async void OnEditRule(object? sender, RoutedEventArgs e)
    {
        if (GroupDetailOf(sender) is not { } d || sender is not Control { Tag: SecurityGroupRuleInfo current })
            return;
        var (groups, cidrs) = VpcContext(d);
        var dialog = new RuleEditorDialog(d.Group, current, current.IsEgress, groups, cidrs);
        if (!await Dialogs.ShowAsync(dialog, Dialogs.OwnerOf(this)) || dialog.Result is not { } updated)
            return;
        if (updated == current.ToSpec())
        {
            Status("Nothing changed.");
            return;
        }
        _ = RunRuleActionAsync(d, $"Changing rule {current.RuleId}",
            () => _session.Gateway.UpdateSecurityGroupRuleAsync(d.Target, d.Group, current, updated, CancellationToken.None));
    }

    private void OnDeleteRule(object? sender, RoutedEventArgs e)
    {
        if (GroupDetailOf(sender) is not { } d || sender is not Control { Tag: SecurityGroupRuleInfo rule })
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
            App.Current.AskToSignInIfNeeded(ex);
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

    private void Status(string text, IBrush? brush = null)
    {
        ActionStatus.Foreground = brush ?? (this.TryFindResource("TextNormal", out var normal) ? normal as IBrush : null);
        ActionStatus.Text = text;
    }

    private void Fail(string what, Exception ex) =>
        Status($"{what}: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}", LevelToBrushConverter.Critical);
}
