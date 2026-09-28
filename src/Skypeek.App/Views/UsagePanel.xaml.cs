using System.Windows;
using System.Windows.Media;
using Skypeek.Core.Models;

namespace Skypeek.App.Views;

/// <summary>
/// "Usage (last 30 days)" section of a resource's details: reads hourly CPU/memory history from CloudWatch on demand
/// and shows min/avg/max, how often it hits 100%, and a sizing recommendation.
/// </summary>
public partial class UsagePanel
{
    private const int Days = 30;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    /// <summary>Reports by resource key; the details panel is rebuilt on every refresh, the analysis should survive that.</summary>
    private static readonly Dictionary<string, UsageReport> Reports = new();

    public static readonly DependencyProperty ResourceProperty = DependencyProperty.Register(
        nameof(Resource), typeof(ResourceStatus), typeof(UsagePanel), new PropertyMetadata(null, (d, _) => ((UsagePanel)d).ShowCached()));

    public ResourceStatus? Resource
    {
        get => (ResourceStatus?)GetValue(ResourceProperty);
        set => SetValue(ResourceProperty, value);
    }

    public UsagePanel() => InitializeComponent();

    /// <summary>Called on lock: usage figures are cleared with everything else on screen.</summary>
    public static void ClearCache() => Reports.Clear();

    private void ShowCached()
    {
        if (Resource is { } r && Reports.TryGetValue(r.ResourceKey, out var report) && DateTime.UtcNow - report.CreatedUtc < CacheFor)
            Show(report);
        else
            ResultPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnAnalyze(object sender, RoutedEventArgs e)
    {
        if (Resource is not { } resource || App.Current.Session is not { } session || session.Settings.FindTarget(resource.TargetId) is not { } target)
            return;
        AnalyzeButton.IsEnabled = false;
        StatusText.Text = $"Reading {Days} days of hourly metrics from CloudWatch…";
        try
        {
            var report = await session.Health.AnalyzeUsageAsync(target, resource, Days, CancellationToken.None);
            Reports[resource.ResourceKey] = report;
            // The panel may have been rebuilt for a newer snapshot of the same resource meanwhile.
            if (Resource?.ResourceKey == resource.ResourceKey)
                Show(report);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read usage: {(ex is Amazon.Runtime.AmazonServiceException a ? $"{a.ErrorCode}: {a.Message}" : ex.Message)}";
        }
        finally
        {
            AnalyzeButton.IsEnabled = true;
        }
    }

    private void Show(UsageReport report)
    {
        StatusText.Text = report.Series.Count == 0 ? "" : $"{report.Series.Count(s => s.HasData)} of {report.Series.Count} series have data.";
        VerdictText.Text = report.VerdictText;
        var color = ((SolidColorBrush)new LevelToBrushConverter().Convert(report.VerdictLevel, typeof(Brush), null, System.Globalization.CultureInfo.InvariantCulture)).Color;
        VerdictBadge.Background = new SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B));
        ReasonsText.Text = report.ReasonText;
        SeriesList.ItemsSource = report.Series;
        RangeText.Text = report.RangeText;
        AnalyzeButton.Content = "Analyze again";
        ResultPanel.Visibility = Visibility.Visible;
    }
}
