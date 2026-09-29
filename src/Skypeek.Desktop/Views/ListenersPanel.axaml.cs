using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skypeek.Desktop.Infrastructure;
using Skypeek.Desktop.Platform;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

/// <summary>
/// Listeners and routing rules of a load balancer, read when its details are shown (not on every poll) and kept for a
/// few minutes because the details panel is rebuilt on each refresh.
/// </summary>
public partial class ListenersPanel : UserControl
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<string, (DateTime At, IReadOnlyList<LbListenerInfo> Listeners)> Cache = new();
    private static readonly HashSet<string> Loading = new();

    public static readonly StyledProperty<LoadBalancerStatus?> ResourceProperty = AvaloniaProperty.Register<ListenersPanel, LoadBalancerStatus?>(nameof(Resource));

    public LoadBalancerStatus? Resource
    {
        get => GetValue(ResourceProperty);
        set => SetValue(ResourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResourceProperty)
            OnResourceChanged();
    }

    public ListenersPanel() => InitializeComponent();

    /// <summary>Called on lock.</summary>
    public static void ClearCache() => Cache.Clear();

    private void OnResourceChanged()
    {
        if (Resource is not { } lb)
            return;
        if (Cache.TryGetValue(lb.ResourceKey, out var cached) && DateTime.UtcNow - cached.At < CacheFor)
            Show(cached.Listeners, cached.At);
        else
            _ = LoadAsync(lb);
    }

    private void OnReload(object? sender, RoutedEventArgs e)
    {
        if (Resource is { } lb)
            _ = LoadAsync(lb);
    }

    private async Task LoadAsync(LoadBalancerStatus lb)
    {
        if (App.Current.Session is not { } session || session.Settings.FindTarget(lb.TargetId) is not { } target)
            return;
        // WPF reuses the panel for another load balancer: never show a stale list meanwhile.
        ListenerList.ItemsSource = null;
        StatusText.Text = "Reading listeners and rules…";
        if (!Loading.Add(lb.ResourceKey))
            return;
        ReloadButton.IsEnabled = false;
        try
        {
            var listeners = await session.Gateway.GetLoadBalancerListenersAsync(target, lb.Snapshot, CancellationToken.None);
            var now = DateTime.UtcNow;
            Cache[lb.ResourceKey] = (now, listeners);
            if (Resource?.ResourceKey == lb.ResourceKey)
                Show(listeners, now);
        }
        catch (Exception ex)
        {
            if (Resource?.ResourceKey == lb.ResourceKey)
                StatusText.Text = $"Could not read listeners: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}";
        }
        finally
        {
            Loading.Remove(lb.ResourceKey);
            ReloadButton.IsEnabled = true;
        }
    }

    private void Show(IReadOnlyList<LbListenerInfo> listeners, DateTime loadedUtc)
    {
        ListenerList.ItemsSource = listeners;
        StatusText.Text = listeners.Count == 0
            ? "No listeners."
            : $"{listeners.Count} listener(s), {listeners.Sum(l => l.Rules.Count)} rule(s) · read {loadedUtc.ToLocalTime():T}. Rules are evaluated by priority, lowest first.";
    }
}
