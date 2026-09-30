using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

/// <summary>
/// A stack's recent operations (create/update/delete with their events), read when its details are shown and kept for
/// a few minutes because the details panel is rebuilt on each refresh.
/// </summary>
public partial class StackHistoryPanel : UserControl
{
    private const int MaxEvents = 300;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<string, (DateTime At, string Status, IReadOnlyList<StackOperation> Operations)> Cache = new();
    private static readonly HashSet<string> Loading = new();

    public static readonly StyledProperty<StackStatus?> ResourceProperty = AvaloniaProperty.Register<StackHistoryPanel, StackStatus?>(nameof(Resource));

    public StackStatus? Resource
    {
        get => GetValue(ResourceProperty);
        set => SetValue(ResourceProperty, value);
    }

    public StackHistoryPanel() => InitializeComponent();

    /// <summary>Called on lock.</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>For the render harness.</summary>
    public static void Prime(StackStatus stack, IReadOnlyList<StackEventInfo> events) =>
        Cache[stack.ResourceKey] = (DateTime.UtcNow, stack.Snapshot.Status, StackRules.Operations(stack.Snapshot.Name, events));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ResourceProperty || Resource is not { } stack)
            return;
        if (Cache.TryGetValue(stack.ResourceKey, out var cached) && DateTime.UtcNow - cached.At < CacheFor && cached.Status == stack.Snapshot.Status)
            Show(cached.Operations, cached.At);
        else
            _ = LoadAsync(stack);
    }

    private void OnReload(object? sender, RoutedEventArgs e)
    {
        if (Resource is { } stack)
            _ = LoadAsync(stack);
    }

    private async Task LoadAsync(StackStatus stack)
    {
        if (App.Current.Session is not { } session || session.Settings.FindTarget(stack.TargetId) is not { } target)
            return;
        OperationList.ItemsSource = null;
        StatusText.Text = "Reading stack events…";
        if (!Loading.Add(stack.ResourceKey))
            return;
        ReloadButton.IsEnabled = false;
        try
        {
            var events = await session.Gateway.GetStackEventsAsync(target, stack.Snapshot.Id, MaxEvents, CancellationToken.None);
            var operations = StackRules.Operations(stack.Snapshot.Name, events);
            var now = DateTime.UtcNow;
            Cache[stack.ResourceKey] = (now, stack.Snapshot.Status, operations);
            if (Resource?.ResourceKey == stack.ResourceKey)
                Show(operations, now);
        }
        catch (Exception ex)
        {
            if (Resource?.ResourceKey == stack.ResourceKey)
                StatusText.Text = $"Could not read stack events: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}";
        }
        finally
        {
            Loading.Remove(stack.ResourceKey);
            ReloadButton.IsEnabled = true;
        }
    }

    private void Show(IReadOnlyList<StackOperation> operations, DateTime loadedUtc)
    {
        OperationList.ItemsSource = operations;
        StatusText.Text = operations.Count == 0
            ? "No events."
            : $"{operations.Count} operation(s), newest first · read {loadedUtc.ToLocalTime():T}. Expand one for its events.";
    }
}
