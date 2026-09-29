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
/// "Usage (last 30 days)" section of a resource's details: reads hourly CPU/memory history from CloudWatch on demand
/// and shows min/avg/max, how often it hits 100%, and a sizing recommendation.
/// </summary>
public partial class UsagePanel : UserControl
{
    private const int Days = 30;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    /// <summary>Reports by resource key; the details panel is rebuilt on every refresh, the analysis should survive that.</summary>
    private static readonly Dictionary<string, UsageReport> Reports = new();

    public static readonly StyledProperty<ResourceStatus?> ResourceProperty = AvaloniaProperty.Register<UsagePanel, ResourceStatus?>(nameof(Resource));

    public ResourceStatus? Resource
    {
        get => GetValue(ResourceProperty);
        set => SetValue(ResourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResourceProperty)
            ShowCached();
    }

    private readonly string _intro;

    public UsagePanel()
    {
        InitializeComponent();
        _intro = StatusText.Text ?? "";
    }

    /// <summary>Called on lock: usage figures are cleared with everything else on screen.</summary>
    public static void ClearCache() => Reports.Clear();

    /// <summary>
    /// WPF reuses this panel when the details switch to another resource of the same type, so everything shown must
    /// come from that resource's own cached report, or be reset.
    /// </summary>
    private void ShowCached()
    {
        if (Resource is { } r && Reports.TryGetValue(r.ResourceKey, out var report) && DateTime.UtcNow - report.CreatedUtc < CacheFor)
        {
            Show(report);
            return;
        }
        StatusText.Text = _intro;
        AnalyzeButton.Content = "Analyze last 30 days";
        AnalyzeButton.IsEnabled = true;
        SeriesList.ItemsSource = null;
        ResultPanel.IsVisible = false;
    }

    private async void OnAnalyze(object? sender, RoutedEventArgs e)
    {
        if (Resource is not { } resource || App.Current.Session is not { } session || session.Settings.FindTarget(resource.TargetId) is not { } target)
            return;
        AnalyzeButton.IsEnabled = false;
        StatusText.Text = $"Reading {Days} days of hourly metrics from CloudWatch…";
        try
        {
            var report = await session.Health.AnalyzeUsageAsync(target, resource, Days, CancellationToken.None);
            Reports[resource.ResourceKey] = report;
            // The panel may show another resource by now (or a newer snapshot of this one).
            if (Resource?.ResourceKey == resource.ResourceKey)
                Show(report);
        }
        catch (Exception ex)
        {
            if (Resource?.ResourceKey == resource.ResourceKey)
                StatusText.Text = $"Could not read usage: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}";
        }
        finally
        {
            if (Resource?.ResourceKey == resource.ResourceKey)
                AnalyzeButton.IsEnabled = true;
        }
    }

    private void Show(UsageReport report)
    {
        StatusText.Text = report.Series.Count == 0 ? "" : $"{report.Series.Count(s => s.HasData)} of {report.Series.Count} series have data.";
        VerdictText.Text = report.VerdictText;
        var color = LevelToBrushConverter.For(report.VerdictLevel).Color;
        VerdictBadge.Background = new SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B));
        ReasonsText.Text = report.ReasonText;
        SeriesList.ItemsSource = report.Series;
        RangeText.Text = report.RangeText;
        AnalyzeButton.Content = "Analyze again";
        ResultPanel.IsVisible = true;
    }
}
