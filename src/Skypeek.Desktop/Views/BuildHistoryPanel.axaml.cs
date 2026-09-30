using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Skypeek.Core.Models;
using Skypeek.Desktop.Platform;

namespace Skypeek.Desktop.Views;

/// <summary>
/// The last builds of a CodeBuild project, read when its details are shown (not on every poll) and kept for a few
/// minutes because the details panel is rebuilt on each refresh.
/// </summary>
public partial class BuildHistoryPanel : UserControl
{
    private const int MaxBuilds = 25;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<string, (DateTime At, IReadOnlyList<CodeBuildRun> Builds)> Cache = new();
    private static readonly HashSet<string> Loading = new();

    public static readonly StyledProperty<CodeBuildStatus?> ResourceProperty = AvaloniaProperty.Register<BuildHistoryPanel, CodeBuildStatus?>(nameof(Resource));

    public CodeBuildStatus? Resource
    {
        get => GetValue(ResourceProperty);
        set => SetValue(ResourceProperty, value);
    }

    public BuildHistoryPanel() => InitializeComponent();

    /// <summary>Called on lock.</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>For the render harness.</summary>
    public static void Prime(string resourceKey, IReadOnlyList<CodeBuildRun> builds) => Cache[resourceKey] = (DateTime.UtcNow, builds);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ResourceProperty || Resource is not { } project)
            return;
        // A newer build than the cached history makes it stale.
        if (Cache.TryGetValue(project.ResourceKey, out var cached) && DateTime.UtcNow - cached.At < CacheFor
            && (project.Snapshot.LatestBuild is null || cached.Builds.Any(b => b.Id == project.Snapshot.LatestBuild.Id && b.Status == project.Snapshot.LatestBuild.Status)))
            Show(cached.Builds, cached.At);
        else
            _ = LoadAsync(project);
    }

    private void OnReload(object? sender, RoutedEventArgs e)
    {
        if (Resource is { } project)
            _ = LoadAsync(project);
    }

    private async Task LoadAsync(CodeBuildStatus project)
    {
        if (App.Current.Session is not { } session || session.Settings.FindTarget(project.TargetId) is not { } target)
            return;
        BuildList.ItemsSource = null;
        StatusText.Text = "Reading builds…";
        if (!Loading.Add(project.ResourceKey))
            return;
        ReloadButton.IsEnabled = false;
        try
        {
            var builds = await session.Gateway.GetCodeBuildHistoryAsync(target, project.Snapshot.Name, MaxBuilds, CancellationToken.None);
            var now = DateTime.UtcNow;
            Cache[project.ResourceKey] = (now, builds);
            if (Resource?.ResourceKey == project.ResourceKey)
                Show(builds, now);
        }
        catch (Exception ex)
        {
            if (Resource?.ResourceKey == project.ResourceKey)
                StatusText.Text = $"Could not read builds: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}";
        }
        finally
        {
            Loading.Remove(project.ResourceKey);
            ReloadButton.IsEnabled = true;
        }
    }

    private void Show(IReadOnlyList<CodeBuildRun> builds, DateTime loadedUtc)
    {
        BuildList.ItemsSource = builds;
        var failed = builds.Count(b => b.IsFailure);
        StatusText.Text = builds.Count == 0
            ? "No builds yet."
            : $"Last {builds.Count} build(s), {failed} failed · read {loadedUtc.ToLocalTime():T}. Expand a build for its phases.";
    }

    private void OnOpenBuild(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is CodeBuildRun run && Resource is { } project)
            Shell.OpenUrl(project.BuildConsoleUrl(run));
    }
}
